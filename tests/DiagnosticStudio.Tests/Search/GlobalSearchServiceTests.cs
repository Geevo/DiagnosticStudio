using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Parsers.Evtx;
using DiagnosticStudio.Search;
using DiagnosticStudio.Tests.Evtx;

namespace DiagnosticStudio.Tests.Search;

public sealed class GlobalSearchServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-gsearch-" + Guid.NewGuid().ToString("N"));

    public GlobalSearchServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- fixtures ----

    private sealed class ListLines : ITextLineSource
    {
        private readonly string[] _lines;

        public ListLines(IEnumerable<string> lines) => _lines = lines.ToArray();

        public int LineCount => _lines.Length;
        public long ByteLength => 0;
        public string EncodingName => "test";
        public IReadOnlyList<string> ReadLines(int startLine, int count) => _lines.Skip(startLine).Take(count).ToArray();
        public IEnumerable<string> EnumerateLines(int startLine = 0) => _lines.Skip(startLine);
    }

    private sealed class FakeLoader : IDocumentLoader
    {
        private readonly Dictionary<Guid, Func<DiagnosticArtifact, DocumentLoadResult>> _documents = new();
        public List<Guid> Loaded { get; } = new();

        public void Set(DiagnosticArtifact artifact, Func<DiagnosticArtifact, DocumentLoadResult> make) =>
            _documents[artifact.Id] = make;

        /// <summary>Simulates slow parsing so tests can observe cancellation of work in progress.</summary>
        public TimeSpan Delay { get; set; }

        public async Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            lock (Loaded)
            {
                Loaded.Add(artifact.Id);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _documents[artifact.Id](artifact);
        }
    }

    private static DiagnosticArtifact Artifact(string name, ArtifactType type, params string[] folders) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OriginalPath = name,
        ExtractedPath = "C:\\unused\\" + name,
        Provenance = new[] { "Bundle.zip" }.Concat(folders).Append(name).ToArray(),
        ArtifactType = type,
    };

    private (DiagnosticArtifact Artifact, FakeLoader Loader) Text(string name, IEnumerable<string> lines, FakeLoader? loader = null, params string[] folders)
    {
        loader ??= new FakeLoader();
        var artifact = Artifact(name, ArtifactType.TextLog, folders);
        var source = new ListLines(lines);
        loader.Set(artifact, a => new DocumentLoadResult(new TextDocument { Artifact = a, Lines = source }, null));
        return (artifact, loader);
    }

    private static void AddRegistry(FakeLoader loader, DiagnosticArtifact artifact, params string[] lines) =>
        loader.Set(artifact, a => new DocumentLoadResult(
            RegFileParser.Parse(a, lines, new ListLines(lines)), null));

    private async Task<(DiagnosticArtifact Artifact, EvtxFile File)> EventLog(FakeLoader loader, string name, IReadOnlyList<TestEvent> events, IEventMessageFormatter? formatter = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".evtx");
        File.WriteAllBytes(path, new EvtxBuilder().Build(events));
        var file = await EvtxFile.OpenAsync(path, formatter, CancellationToken.None);
        var artifact = Artifact(name, ArtifactType.EventLog);
        loader.Set(artifact, a => new DocumentLoadResult(new EventLogDocument { Artifact = a, Source = file }, null));
        return (artifact, file);
    }

    private static async Task<List<SearchUpdate>> Run(
        FakeLoader loader,
        IReadOnlyList<DiagnosticArtifact> artifacts,
        string query,
        SearchOptions? options = null,
        bool matchCase = false,
        CancellationToken ct = default)
    {
        var parsed = SearchQueryParser.Parse(query, matchCase);
        Assert.Empty(parsed.Errors);
        var updates = new List<SearchUpdate>();
        await foreach (var update in new GlobalSearchService(loader).SearchAsync(artifacts, parsed.Query, options ?? new SearchOptions(), ct))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static List<ArtifactSearchResult> Results(IEnumerable<SearchUpdate> updates) =>
        updates.Where(u => u.Result is not null).Select(u => u.Result!).ToList();

    // ---- text ----

    [Fact]
    public async Task Text_lines_are_found_with_locations_previews_and_highlight_offsets()
    {
        var (log, loader) = Text("agent.log", new[]
        {
            "2026-07-23 14:12:00 INFO started",
            "    2026-07-23 14:12:01 ERROR failed with 0x80072F8F while connecting",
            "2026-07-23 14:12:02 INFO done",
            "retry: 0x80072f8f again",
        }, folders: "Logs");

        var updates = await Run(loader, new[] { log }, "0x80072F8F");

        var result = Assert.Single(Results(updates));
        Assert.Equal(2, result.TotalHits);
        Assert.False(result.IsTruncated);
        Assert.Equal(new long[] { 2, 4 }, result.Hits.Select(h => h.Location.NumericPosition!.Value));
        Assert.All(result.Hits, h => Assert.Equal(DiagnosticLocationKind.Line, h.Location.Kind));
        Assert.All(result.Hits, h => Assert.Equal(log.Id, h.ArtifactId));

        var first = result.Hits[0];
        Assert.StartsWith("2026-07-23", first.Preview); // leading whitespace trimmed
        Assert.Equal("0x80072F8F", first.Preview.Substring(first.MatchStart, first.MatchLength));
        var second = result.Hits[1]; // matched case-insensitively; offset points at the original text
        Assert.Equal("0x80072f8f", second.Preview.Substring(second.MatchStart, second.MatchLength));
    }

    [Fact]
    public async Task Match_case_narrows_text_hits()
    {
        var (log, loader) = Text("a.log", new[] { "Error one", "error two", "ERROR three" });

        Assert.Equal(3, Results(await Run(loader, new[] { log }, "error")).Single().TotalHits);
        Assert.Equal(1, Results(await Run(loader, new[] { log }, "error", matchCase: true)).Single().TotalHits);
    }

    [Fact]
    public async Task Hits_beyond_the_per_artifact_cap_are_counted_but_not_listed()
    {
        var (log, loader) = Text("big.log", Enumerable.Range(1, 800).Select(i => $"line {i} needle"));

        var result = Results(await Run(loader, new[] { log }, "needle", new SearchOptions { MaxHitsPerArtifact = 500 })).Single();

        Assert.Equal(800, result.TotalHits);
        Assert.Equal(500, result.Hits.Count);
        Assert.True(result.IsTruncated);
        Assert.Equal(500, result.Hits[^1].Location.NumericPosition);
    }

    [Fact]
    public async Task Long_lines_are_cropped_around_the_match()
    {
        var line = new string('x', 400) + " NEEDLE " + new string('y', 400);
        var (log, loader) = Text("long.log", new[] { line });

        var hit = Results(await Run(loader, new[] { log }, "needle", new SearchOptions { PreviewLength = 120 })).Single().Hits.Single();

        Assert.True(hit.Preview.Length <= 125);
        Assert.StartsWith("\u2026", hit.Preview);
        Assert.EndsWith("\u2026", hit.Preview);
        Assert.Equal("NEEDLE", hit.Preview.Substring(hit.MatchStart, hit.MatchLength));
    }

    // ---- registry ----

    private const string RegHeader = "Windows Registry Editor Version 5.00";

    [Fact]
    public async Task Registry_hits_cover_key_names_value_names_and_value_data_with_exact_locations()
    {
        var loader = new FakeLoader();
        var reg = Artifact("policy.reg", ArtifactType.RegistryExport);
        AddRegistry(
            loader, reg,
            RegHeader,
            @"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\WindowsUpdate]",
            "\"WUServer\"=\"http://wsus.contoso.test:8530\"",
            "@=\"default wsus text\"",
            @"[HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\WsusClient]",
            "\"Other\"=\"x\"");

        var result = Results(await Run(loader, new[] { reg }, "wsus")).Single();

        Assert.Equal(3, result.TotalHits); // WUServer data, default value data, WsusClient key name
        var byField = result.Hits.ToDictionary(h => (h.MatchField, h.Location.Member ?? "<key>"));

        // value name "WUServer" does not contain wsus; its data does
        var data = byField[("value data", "WUServer")];
        Assert.Equal(DiagnosticLocationKind.Registry, data.Location.Kind);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\WindowsUpdate", data.Location.Identifier);
        Assert.Equal("wsus", data.Preview.Substring(data.MatchStart, data.MatchLength));
        Assert.Contains("= http://wsus.contoso.test:8530", data.Preview);

        var defaultValue = byField[("value data", "")]; // the default value: Member is the empty string
        Assert.Contains("(Default)", defaultValue.Preview);

        var key = byField[("key", "<key>")];
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\WsusClient", key.Location.Identifier);
        Assert.Null(key.Location.Member);
        Assert.Equal("Wsus", key.Preview.Substring(key.MatchStart, key.MatchLength));
    }

    [Fact]
    public async Task Value_name_matches_are_labelled_and_highlighted_in_the_name()
    {
        var loader = new FakeLoader();
        var reg = Artifact("t.reg", ArtifactType.RegistryExport);
        AddRegistry(loader, reg, RegHeader, @"[HKEY_CURRENT_USER\K]", "\"ProxyServer\"=\"p:8080\"");

        var hit = Results(await Run(loader, new[] { reg }, "proxy")).Single().Hits.Single();

        Assert.Equal("value name", hit.MatchField);
        Assert.Equal("Proxy", hit.Preview.Substring(hit.MatchStart, hit.MatchLength));
        Assert.Equal("ProxyServer", hit.Location.Member);
    }

    // ---- event logs ----

    private static readonly TestEvent[] Events =
    {
        new(100, "Service Control Manager", 7036, EventLevels.Information, T0, "PC", "Print Spooler", "running"),
        new(101, "Service Control Manager", 7031, EventLevels.Error, T0.AddSeconds(1), "PC", "Print Spooler", "terminated unexpectedly"),
        new(102, "Schannel", 36871, EventLevels.Error, T0.AddSeconds(2), "PC", "TLS client credential", "failed"),
        new(103, "Schannel", 36874, EventLevels.Warning, T0.AddSeconds(3), "PC", "TLS 1.2 connection", "received"),
    };

    [Fact]
    public async Task Event_hits_navigate_by_record_id_and_carry_a_timestamp_and_summary()
    {
        var loader = new FakeLoader();
        var (log, _) = await EventLog(loader, "System.evtx", Events);

        var result = Results(await Run(loader, new[] { log }, "print spooler")).Single();

        Assert.Equal(2, result.TotalHits);
        Assert.Equal(new long[] { 100, 101 }, result.Hits.Select(h => h.Location.NumericPosition!.Value));
        Assert.All(result.Hits, h => Assert.Equal(DiagnosticLocationKind.EventRecord, h.Location.Kind));
        var hit = result.Hits[1];
        Assert.Equal(T0.AddSeconds(1), hit.Timestamp);
        Assert.StartsWith("Service Control Manager \u00B7 7031 \u00B7 Error", hit.Preview);
        Assert.Equal("Print Spooler", hit.Preview.Substring(hit.MatchStart, hit.MatchLength).Replace("print spooler", "Print Spooler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Structured_operators_filter_event_logs_and_exclude_other_artifact_types()
    {
        var loader = new FakeLoader();
        var (events, _) = await EventLog(loader, "System.evtx", Events);
        var (text, _) = Text("agent.log", new[] { "Schannel error in text log" }, loader);

        var byId = Results(await Run(loader, new[] { events, text }, "eventid:7031"));
        Assert.Equal(new long[] { 101 }, byId.Single().Hits.Select(h => h.Location.NumericPosition!.Value));
        Assert.Equal(events.Id, byId.Single().Artifact.Id);

        var byProviderAndLevel = Results(await Run(loader, new[] { events, text }, "provider:schan level:error"));
        Assert.Equal(new long[] { 102 }, byProviderAndLevel.Single().Hits.Select(h => h.Location.NumericPosition!.Value));

        loader.Loaded.Clear();
        var withText = Results(await Run(loader, new[] { events, text }, "provider:schannel tls"));
        Assert.Equal(2, withText.Single().TotalHits);
        Assert.DoesNotContain(text.Id, loader.Loaded); // the text log is never opened for event-only queries
    }

    private sealed class MessageFormatter : IEventMessageFormatter
    {
        public string? Format(string provider, uint eventId, int? qualifiers, int version, IReadOnlyList<EventDataItem> data) =>
            provider == "Schannel" ? "A fatal alert was generated: certificate rejected" : null;
    }

    [Fact]
    public async Task Message_text_is_only_searched_when_the_option_is_set()
    {
        var loader = new FakeLoader();
        var (log, _) = await EventLog(loader, "System.evtx", Events, new MessageFormatter());

        Assert.Empty(Results(await Run(loader, new[] { log }, "certificate rejected")));

        var with = Results(await Run(loader, new[] { log }, "certificate rejected", new SearchOptions { IncludeEventMessages = true })).Single();
        Assert.Equal(new long[] { 102, 103 }, with.Hits.Select(h => h.Location.NumericPosition!.Value));
        Assert.Contains("A fatal alert was generated: certificate rejected", with.Hits[0].Preview);
    }

    // ---- file names and artifact listing ----

    [Fact]
    public async Task File_names_and_provenance_are_searched_and_can_be_switched_off()
    {
        var loader = new FakeLoader();
        var (log, _) = Text("agentexecutor.log", new[] { "nothing relevant" }, loader, "mdmlogs.cab", "Logs");

        var on = Results(await Run(loader, new[] { log }, "mdmlogs")).Single();
        var hit = Assert.Single(on.Hits);
        Assert.Equal("file name", hit.MatchField);
        Assert.Equal(DiagnosticLocationKind.Artifact, hit.Location.Kind);
        Assert.Contains("mdmlogs.cab", hit.Preview);

        var off = Results(await Run(loader, new[] { log }, "mdmlogs", new SearchOptions { IncludeFileNames = false }));
        Assert.Empty(off);
    }

    [Fact]
    public async Task A_file_name_hit_and_content_hits_share_one_result()
    {
        var (log, loader) = Text("agent.log", new[] { "agent started" });

        var result = Results(await Run(loader, new[] { log }, "agent")).Single();

        Assert.Equal(2, result.TotalHits);
        Assert.Equal("file name", result.Hits[0].MatchField);
        Assert.Equal(DiagnosticLocationKind.Line, result.Hits[1].Location.Kind);
    }

    [Fact]
    public async Task Type_filter_restricts_artifacts_and_an_operator_only_query_lists_them()
    {
        var loader = new FakeLoader();
        var (log, _) = Text("agent.log", new[] { "x" }, loader);
        var reg = Artifact("t.reg", ArtifactType.RegistryExport);
        AddRegistry(loader, reg, RegHeader, @"[HKEY_CURRENT_USER\K]");
        var (events, _) = await EventLog(loader, "System.evtx", Events);

        var listed = Results(await Run(loader, new[] { log, reg, events }, "type:evtx,reg"));

        Assert.Equal(new[] { "System.evtx", "t.reg" }, listed.Select(r => r.Artifact.Name).OrderBy(n => n));
        Assert.All(listed, r => Assert.Equal("artifact", r.Hits.Single().MatchField));
        Assert.DoesNotContain(log.Id, loader.Loaded);
    }

    // ---- robustness ----

    [Fact]
    public async Task Binary_archive_and_trace_artifacts_are_never_opened()
    {
        var loader = new FakeLoader();
        var artifacts = new[]
        {
            Artifact("tool.bin", ArtifactType.Binary),
            Artifact("logs.cab", ArtifactType.Archive),
            Artifact("trace.etl", ArtifactType.Trace),
        };

        var updates = await Run(loader, artifacts, "anything");

        Assert.Empty(loader.Loaded);
        Assert.Equal(3, updates.Count);
        Assert.Empty(Results(updates));
    }

    [Fact]
    public async Task A_failing_artifact_is_reported_and_the_others_are_still_searched()
    {
        var (good, loader) = Text("good.log", new[] { "needle" });
        var bad = Artifact("bad.log", ArtifactType.TextLog);
        loader.Set(bad, _ => throw new InvalidOperationException("disk gremlin"));
        var failed = Artifact("failed.log", ArtifactType.TextLog);
        loader.Set(failed, a => new DocumentLoadResult(new UnsupportedDocument { Artifact = a, Reason = "x" }, "ParserX failed: boom"));

        var updates = await Run(loader, new[] { good, bad, failed }, "needle");

        Assert.Equal(good.Id, Assert.Single(Results(updates)).Artifact.Id);
        var issues = updates.Where(u => u.Issue is not null).Select(u => u.Issue!).ToList();
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Artifact.Id == bad.Id && i.Message.Contains("disk gremlin"));
        Assert.Contains(issues, i => i.Artifact.Id == failed.Id && i.Message.Contains("ParserX failed"));
    }

    [Fact]
    public async Task Progress_counts_every_artifact_and_hits_accumulate()
    {
        var loader = new FakeLoader();
        var artifacts = Enumerable.Range(0, 12).Select(i => Text($"f{i}.log", new[] { "needle", "x", "needle" }, loader).Artifact).ToList();

        var updates = await Run(loader, artifacts, "needle", new SearchOptions { IncludeFileNames = false, MaxParallelism = 4 });

        Assert.Equal(12, updates.Count);
        Assert.Equal(Enumerable.Range(1, 12), updates.Select(u => u.ArtifactsSearched));
        Assert.All(updates, u => Assert.Equal(12, u.ArtifactsTotal));
        Assert.Equal(24, updates[^1].TotalHits);
        Assert.Equal(updates.Select(u => u.TotalHits).OrderBy(h => h), updates.Select(u => u.TotalHits));
    }

    [Fact]
    public async Task An_empty_query_yields_nothing()
    {
        var (log, loader) = Text("a.log", new[] { "x" });

        Assert.Empty(await Run(loader, new[] { log }, "   "));
        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public async Task Cancelling_stops_the_search()
    {
        // Each artifact takes a moment, so the search is still running when the cancel arrives. With instant
        // loading the producer can finish everything first and no cancellation is ever observed.
        var loader = new FakeLoader { Delay = TimeSpan.FromMilliseconds(10) };
        var artifacts = Enumerable.Range(0, 50).Select(i => Text($"f{i}.log", Enumerable.Repeat("needle", 50), loader).Artifact).ToList();
        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new GlobalSearchService(loader).SearchAsync(
                               artifacts, SearchQueryParser.Parse("needle").Query, new SearchOptions { MaxParallelism = 1 }, cts.Token))
            {
                if (++seen == 3)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.True(seen < 50);
    }

    [Fact]
    public async Task Abandoning_the_enumeration_early_stops_the_work_without_hanging()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromMilliseconds(20) };
        var artifacts = Enumerable.Range(0, 200).Select(i => Text($"f{i}.log", new[] { "needle" }, loader).Artifact).ToList();

        await foreach (var _ in new GlobalSearchService(loader).SearchAsync(
                           artifacts, SearchQueryParser.Parse("needle").Query, new SearchOptions { MaxParallelism = 1 }, CancellationToken.None))
        {
            break;
        }

        // Reaching here means disposal cancelled the producer and waited for it.
        Assert.True(loader.Loaded.Count < 200);
    }

    // ---- crop helper ----

    [Theory]
    [InlineData(10, 4, 100)]
    [InlineData(0, 6, 40)]
    [InlineData(300, 6, 80)]
    [InlineData(494, 6, 50)]
    public void Crop_keeps_the_match_visible_and_its_offset_correct(int matchAt, int matchLength, int max)
    {
        var text = new string('a', matchAt) + new string('M', matchLength) + new string('b', 500 - matchAt - matchLength);

        var (cropped, start) = GlobalSearchService.Crop(text, matchAt, matchLength, max);

        Assert.True(cropped.Length <= max + 2);
        Assert.Equal(new string('M', matchLength), cropped.Substring(start, matchLength));
    }

    [Fact]
    public void Crop_leaves_short_text_untouched()
    {
        Assert.Equal(("short text", 6), GlobalSearchService.Crop("short text", 6, 4, 100));
    }
}
