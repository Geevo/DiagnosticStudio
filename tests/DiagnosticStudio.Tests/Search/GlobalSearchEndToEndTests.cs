using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Search;
using DiagnosticStudio.Tests.Evtx;
using DiagnosticStudio.Tests.Ingestion;

namespace DiagnosticStudio.Tests.Search;

/// <summary>A nested bundle through the real ingestor, real parsers and the real search service.</summary>
public sealed class GlobalSearchEndToEndTests : IDisposable
{
    private const string Code = "0x80072F8F";
    private static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);
    private readonly TestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private byte[] EventLogBytes() => new EvtxBuilder().Build(new[]
    {
        new TestEvent(7001, "Microsoft-Windows-WinINet", 5, EventLevels.Error, T0, "PC", "request failed", Code),
        new TestEvent(7002, "Microsoft-Windows-WinINet", 6, EventLevels.Information, T0.AddSeconds(1), "PC", "all good", "ok"),
    });

    private static byte[] Utf16Reg(params string[] lines) =>
        new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(string.Join("\r\n", lines))).ToArray();

    private DocumentLoader RealLoader() => new(new IDiagnosticParser[]
    {
        new RegFileParser(), new EvtxParser(), new TextLogParser(), new UnsupportedArtifactParser(),
    });

    [Fact]
    public async Task One_query_finds_the_same_code_in_logs_registry_and_event_logs_inside_nested_archives()
    {
        var inner = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddText(z, "Logs/agent.log", $"2026-07-23 14:12:00 INFO start\n2026-07-23 14:12:01 ERROR sync failed {Code}\n");
            TestWorkspace.AddBytes(z, "Events/WinINet.evtx", EventLogBytes());
        });
        var bundle = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "mdmlogs.zip", inner);
            TestWorkspace.AddBytes(z, "Registry/Internet_Settings.reg", Utf16Reg(
                "Windows Registry Editor Version 5.00",
                "",
                @"[HKEY_CURRENT_USER\Software\Contoso]",
                $"\"LastError\"=\"{Code}\"",
                "\"Other\"=\"nothing\""));
            TestWorkspace.AddText(z, "Command/ipconfig_all_output.log", "Windows IP Configuration\nHost Name . . : PC-02341\n");
        });
        var zipPath = _ws.PathFor("Bundle.zip");
        File.WriteAllBytes(zipPath, bundle);

        using var workspace = await TestWorkspace.CreateIngestor().IngestAsync(zipPath, _ws.Options(), null, CancellationToken.None);
        var service = new GlobalSearchService(RealLoader());

        var results = new List<ArtifactSearchResult>();
        await foreach (var update in service.SearchAsync(
                           workspace.Artifacts, SearchQueryParser.Parse(Code).Query, new SearchOptions(), CancellationToken.None))
        {
            Assert.Null(update.Issue);
            if (update.Result is not null)
            {
                results.Add(update.Result);
            }
        }

        Assert.Equal(new[] { "Internet_Settings.reg", "WinINet.evtx", "agent.log" }, results.Select(r => r.Artifact.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, results.Sum(r => r.TotalHits));

        // Provenance survives nesting: the log is inside the nested archive.
        var log = results.Single(r => r.Artifact.Name == "agent.log");
        Assert.Equal(new[] { "Bundle.zip", "mdmlogs.zip", "Logs", "agent.log" }, log.Artifact.Provenance);

        // Every hit points at evidence that the matching viewer can resolve.
        var logHit = log.Hits.Single();
        Assert.Equal(DiagnosticLocation.ForLine(log.Artifact.Id, 2), logHit.Location);
        Assert.Equal(Code, logHit.Preview.Substring(logHit.MatchStart, logHit.MatchLength));

        var reg = results.Single(r => r.Artifact.Name == "Internet_Settings.reg");
        Assert.Equal(
            DiagnosticLocation.ForRegistry(reg.Artifact.Id, @"HKEY_CURRENT_USER\Software\Contoso", "LastError"),
            reg.Hits.Single().Location);

        var events = results.Single(r => r.Artifact.Name == "WinINet.evtx");
        Assert.Equal(DiagnosticLocation.ForEventRecord(events.Artifact.Id, 7001), events.Hits.Single().Location);

        // The locations resolve inside the real documents.
        var loader = RealLoader();
        var regDoc = (RegistryDocument)(await loader.LoadAsync(reg.Artifact, CancellationToken.None)).Document;
        Assert.Equal(Code, regDoc.FindKey(reg.Hits[0].Location.Identifier!)!.FindValue("LastError")!.DisplayValue);
        var eventDoc = (EventLogDocument)(await loader.LoadAsync(events.Artifact, CancellationToken.None)).Document;
        Assert.NotEqual(-1, eventDoc.Source.FindByRecordId(7001));
    }

    [Fact]
    public async Task Operators_and_file_name_search_work_over_the_ingested_bundle()
    {
        var bundle = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "Events/WinINet.evtx", EventLogBytes());
            TestWorkspace.AddText(z, "Logs/agent.log", "mentions WinINet in passing\n");
            TestWorkspace.AddBytes(z, "tools/helper.exe", new byte[] { 0x4D, 0x5A, 0, 0, 0, 1, 2, 3 });
        });
        var zipPath = _ws.PathFor("Bundle.zip");
        File.WriteAllBytes(zipPath, bundle);
        using var workspace = await TestWorkspace.CreateIngestor().IngestAsync(zipPath, _ws.Options(), null, CancellationToken.None);
        var service = new GlobalSearchService(RealLoader());

        async Task<List<ArtifactSearchResult>> Search(string text)
        {
            var found = new List<ArtifactSearchResult>();
            await foreach (var u in service.SearchAsync(workspace.Artifacts, SearchQueryParser.Parse(text).Query, new SearchOptions(), CancellationToken.None))
            {
                if (u.Result is not null)
                {
                    found.Add(u.Result);
                }
            }

            return found;
        }

        var byLevel = await Search("level:error");
        var errorEvent = Assert.Single(byLevel);
        Assert.Equal("WinINet.evtx", errorEvent.Artifact.Name);
        Assert.Equal(7001, errorEvent.Hits.Single().Location.NumericPosition);

        var names = await Search("helper");
        Assert.Equal("helper.exe", Assert.Single(names).Artifact.Name); // found by name even though binary content is not searched

        var registryOnly = await Search("type:reg");
        Assert.Empty(registryOnly);
    }
}
