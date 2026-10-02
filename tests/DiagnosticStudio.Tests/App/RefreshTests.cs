using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

/// <summary>Reading a file from disk again: F5, the button on the tab, and the button on the "not cleanly closed" banner.</summary>
public sealed class RefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly Ingestor _ingestor = new();

    public RefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Ingestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private sealed class CountingLoader : IDocumentLoader
    {
        private readonly IDocumentLoader _inner;

        public CountingLoader(IDocumentLoader inner) => _inner = inner;

        public int Loads { get; private set; }

        public Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            Loads++;
            return _inner.LoadAsync(artifact, cancellationToken);
        }
    }

    private DiagnosticArtifact WriteLog(string name, int lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, lines).Select(i => name + " line " + i));
        return new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = ArtifactType.TextLog,
        };
    }

    private async Task<(DocumentHostViewModel Host, CountingLoader Counting, CachingDocumentLoader Cache)> Create(params DiagnosticArtifact[] artifacts)
    {
        var counting = new CountingLoader(new DocumentLoader(new IDiagnosticParser[] { new TextLogParser(), new UnsupportedArtifactParser() }));
        var cache = new CachingDocumentLoader(counting);
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, cache, new NavigationHistory(), _output, cache);
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, artifacts, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
        return (host, counting, cache);
    }

    private static async Task<ArtifactDocumentViewModel> Loaded(DocumentHostViewModel host, DiagnosticArtifact artifact)
    {
        var document = host.Documents.OfType<ArtifactDocumentViewModel>().Single(d => d.Artifact.Id == artifact.Id);
        for (var i = 0; i < 300 && document.IsLoading; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(document.IsLoading);
        return document;
    }

    // ---- the cache ----

    [Fact]
    public async Task Invalidating_a_file_makes_the_next_load_read_it_again_even_if_it_looks_unchanged()
    {
        var log = WriteLog("a.log", 5);
        var counting = new CountingLoader(new DocumentLoader(new IDiagnosticParser[] { new TextLogParser(), new UnsupportedArtifactParser() }));
        var cache = new CachingDocumentLoader(counting);

        await cache.LoadAsync(log, CancellationToken.None);
        await cache.LoadAsync(log, CancellationToken.None);
        Assert.Equal(1, counting.Loads); // cached

        cache.Invalidate(log.Id);
        await cache.LoadAsync(log, CancellationToken.None);

        Assert.Equal(2, counting.Loads);
    }

    [Fact]
    public async Task Invalidating_one_file_leaves_the_others_cached()
    {
        var a = WriteLog("a.log", 5);
        var b = WriteLog("b.log", 5);
        var counting = new CountingLoader(new DocumentLoader(new IDiagnosticParser[] { new TextLogParser(), new UnsupportedArtifactParser() }));
        var cache = new CachingDocumentLoader(counting);
        await cache.LoadAsync(a, CancellationToken.None);
        await cache.LoadAsync(b, CancellationToken.None);

        cache.Invalidate(a.Id);

        Assert.Equal(1, cache.Statistics.Entries);
        await cache.LoadAsync(b, CancellationToken.None);
        Assert.Equal(2, counting.Loads);
    }

    [Fact]
    public void Invalidating_something_that_is_not_cached_is_harmless()
    {
        var cache = new CachingDocumentLoader(new CountingLoader(new DocumentLoader(new IDiagnosticParser[] { new UnsupportedArtifactParser() })));

        cache.Invalidate(Guid.NewGuid());

        Assert.Equal(0, cache.Statistics.Entries);
    }

    // ---- the tab ----

    [Fact]
    public async Task Refresh_shows_what_is_in_the_file_now()
    {
        var log = WriteLog("a.log", 10);
        var (host, _, _) = await Create(log);
        host.OpenArtifact(log);
        var document = await Loaded(host, log);
        Assert.Equal(10, ((TextViewerViewModel)document.Viewer!).Lines.Count);

        File.AppendAllLines(log.ExtractedPath!, new[] { "new line 11", "new line 12" });
        await host.RefreshCommand.ExecuteAsync(document);

        Assert.Equal(12, ((TextViewerViewModel)document.Viewer!).Lines.Count);
        Assert.False(document.IsLoading);
    }

    [Fact]
    public async Task Refresh_re_reads_even_when_the_file_has_not_changed()
    {
        var log = WriteLog("a.log", 10);
        var (host, counting, _) = await Create(log);
        host.OpenArtifact(log);
        var document = await Loaded(host, log);
        var before = counting.Loads;

        await host.RefreshCommand.ExecuteAsync(document);

        Assert.Equal(before + 1, counting.Loads);
    }

    [Fact]
    public async Task Refresh_returns_to_the_line_the_engineer_was_on()
    {
        var log = WriteLog("a.log", 200);
        var (host, _, _) = await Create(log);
        host.OpenArtifact(log);
        var document = await Loaded(host, log);
        ((TextViewerViewModel)document.Viewer!).SetCurrentLineFromSelection(150);

        File.AppendAllLines(log.ExtractedPath!, new[] { "more" });
        await host.RefreshCommand.ExecuteAsync(document);

        Assert.Equal(150, ((TextViewerViewModel)document.Viewer!).CurrentLine);
    }

    [Fact]
    public async Task Refresh_with_no_parameter_refreshes_the_active_tab()
    {
        var log = WriteLog("a.log", 3);
        var (host, counting, _) = await Create(log);
        host.OpenArtifact(log);
        await Loaded(host, log);
        var before = counting.Loads;

        await host.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, counting.Loads);
    }

    [Fact]
    public async Task Refresh_on_the_overview_does_nothing()
    {
        var (host, counting, _) = await Create(WriteLog("a.log", 3));

        await host.RefreshCommand.ExecuteAsync(host.ActiveDocument);

        Assert.Equal(0, counting.Loads);
    }

    [Fact]
    public async Task While_refreshing_the_old_content_stays_and_the_tab_says_it_is_loading()
    {
        var log = WriteLog("a.log", 10);
        var (host, _, _) = await Create(log);
        host.OpenArtifact(log);
        var document = await Loaded(host, log);
        var oldViewer = document.Viewer;

        var refresh = host.RefreshCommand.ExecuteAsync(document);
        Assert.NotNull(document.Viewer); // never blank while it reads

        await refresh;
        Assert.NotSame(oldViewer, document.Viewer);
    }

    [Fact]
    public async Task A_file_that_became_empty_is_flagged_after_a_refresh()
    {
        var log = WriteLog("a.log", 10);
        var (host, _, _) = await Create(log);
        host.OpenArtifact(log);
        var document = await Loaded(host, log);
        Assert.False(document.IsEmpty);

        File.WriteAllBytes(log.ExtractedPath!, Array.Empty<byte>());
        await host.RefreshCommand.ExecuteAsync(document);

        Assert.True(document.IsEmpty);
    }
}
