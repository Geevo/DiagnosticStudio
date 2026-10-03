using System.Text;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Search;
using DiagnosticStudio.Tests.Evtx;
using DiagnosticStudio.Tests.Ingestion;

namespace DiagnosticStudio.Tests.App;

/// <summary>Rules, global search and the viewers share one parse of each artifact through the document cache.</summary>
public sealed class SharedParsingTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);
    private readonly TestWorkspace _ws = new();
    private readonly OutputViewModel _output = new();

    public void Dispose() => _ws.Dispose();

    private sealed class CountingLoader : IDocumentLoader
    {
        private readonly IDocumentLoader _inner;
        private readonly Dictionary<Guid, int> _counts = new();

        public CountingLoader(IDocumentLoader inner) => _inner = inner;

        public int ParsesOf(Guid id)
        {
            lock (_counts)
            {
                return _counts.GetValueOrDefault(id);
            }
        }

        public int Total
        {
            get
            {
                lock (_counts)
                {
                    return _counts.Values.Sum();
                }
            }
        }

        public Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            lock (_counts)
            {
                _counts[artifact.Id] = _counts.GetValueOrDefault(artifact.Id) + 1;
            }

            return _inner.LoadAsync(artifact, cancellationToken);
        }
    }

    private static byte[] Utf16Reg(params string[] lines) =>
        new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(string.Join("\r\n", lines))).ToArray();

    private static DocumentLoader RealLoader() => new(new IDiagnosticParser[]
    {
        new RegFileParser(), new EvtxParser(), new TextLogParser(), new UnsupportedArtifactParser(),
    });

    private async Task<InvestigationWorkspace> BundleAsync()
    {
        var system = new EvtxBuilder().Build(new[]
        {
            new TestEvent(11, "Service Control Manager", 7031, EventLevels.Error, T0, "PC", "Agent Service", "1"),
            new TestEvent(12, "Service Control Manager", 7031, EventLevels.Error, T0.AddMinutes(1), "PC", "Agent Service", "2"),
        });
        var zip = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "Events/System.evtx", system);
            TestWorkspace.AddText(z, "Logs/agent.log", "2026-07-23 14:12:00 INFO start\n2026-07-23 14:12:01 ERROR needle one\n");
            TestWorkspace.AddBytes(z, "Registry/system.reg", Utf16Reg(
                "Windows Registry Editor Version 5.00",
                "",
                @"[HKEY_LOCAL_MACHINE\SOFTWARE\Contoso]",
                "\"Note\"=\"needle two\""));
        });
        File.WriteAllBytes(_ws.PathFor("Bundle.zip"), zip);
        return await TestWorkspace.CreateIngestor().IngestAsync(_ws.PathFor("Bundle.zip"), _ws.Options(), null, CancellationToken.None);
    }

    [Fact]
    public async Task Rules_search_and_the_viewer_parse_each_artifact_once_between_them()
    {
        using var workspace = await BundleAsync();
        var counting = new CountingLoader(RealLoader());
        var cache = new CachingDocumentLoader(counting);
        var parsed = workspace.Artifacts.Where(a => a.ArtifactType is ArtifactType.EventLog or ArtifactType.TextLog or ArtifactType.RegistryExport).ToList();
        Assert.Equal(3, parsed.Count);

        // 1. rules
        var findings = await new FindingsService(cache, DefaultRules.Create()).EvaluateAsync(workspace.Artifacts, null, CancellationToken.None);
        Assert.NotEmpty(findings.Findings);

        // 2. global search, twice, with different queries
        var service = new GlobalSearchService(cache);
        foreach (var text in new[] { "needle", "agent" })
        {
            await foreach (var _ in service.SearchAsync(workspace.Artifacts, SearchQueryParser.Parse(text).Query, new SearchOptions(), CancellationToken.None))
            {
            }
        }

        // 3. a viewer opening every artifact
        var workspaceVm = new WorkspaceViewModel(new StaticIngestor(workspace), _output);
        await workspaceVm.OpenAsync("ignored");
        var host = new DocumentHostViewModel(workspaceVm, cache, new NavigationHistory(), _output, cache);
        foreach (var artifact in parsed)
        {
            host.OpenArtifact(artifact);
        }

        await Eventually(() => host.Documents.OfType<ArtifactDocumentViewModel>().All(d => !d.IsLoading));

        foreach (var artifact in parsed)
        {
            Assert.Equal(1, counting.ParsesOf(artifact.Id));
        }

        Assert.Equal(parsed.Count, counting.Total);
        Assert.Equal(parsed.Count, cache.Statistics.Entries);
    }

    [Fact]
    public async Task Changing_the_bundle_releases_the_parsed_documents()
    {
        using var workspace = await BundleAsync();
        var cache = new CachingDocumentLoader(RealLoader());
        var workspaceVm = new WorkspaceViewModel(new StaticIngestor(workspace), _output);
        await workspaceVm.OpenAsync("ignored");
        var host = new DocumentHostViewModel(workspaceVm, cache, new NavigationHistory(), _output, cache);
        host.OpenArtifact(workspace.Artifacts.First(a => a.ArtifactType == ArtifactType.TextLog));
        await Eventually(() => cache.Statistics.Entries == 1);

        workspaceVm.Close();

        Assert.Equal(0, cache.Statistics.Entries);
        Assert.Equal(0, cache.Statistics.EstimatedBytes);
    }

    private sealed class StaticIngestor : IBundleIngestor
    {
        private readonly InvestigationWorkspace _workspace;

        public StaticIngestor(InvestigationWorkspace workspace) => _workspace = workspace;

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(_workspace);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "condition was not reached in time");
    }
}
