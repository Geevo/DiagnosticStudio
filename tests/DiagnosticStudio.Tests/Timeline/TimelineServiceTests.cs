using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Timeline;
using DiagnosticStudio.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Timeline;

public class TimelineServiceTests
{
    private static DocumentLoadResult Ok(DiagnosticDocument document) => new(document, null);

    private static async Task<TimelineIndex> Build(FakeLoader loader, params DiagnosticArtifact[] artifacts) =>
        await new TimelineService(loader).BuildAsync(artifacts, null, CancellationToken.None);

    private static (DiagnosticArtifact Artifact, FakeLoader Loader) TextLog(string name, params string[] lines)
    {
        var artifact = Artifact(name, ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Ok(Text(artifact, lines)));
        return (artifact, loader);
    }

    [Fact]
    public async Task Timestamped_lines_become_entries_with_their_line_numbers()
    {
        var (log, loader) = TextLog(
            "agent.log",
            "2026-07-23 14:12:00 INFO started",
            "    at Foo.Bar()",
            "2026-07-23 14:12:05 ERROR failed",
            "free text with no time at all in it",
            "2026-07-23 14:12:09 WARN slow");

        var index = await Build(loader, log);

        var source = Assert.Single(index.Sources);
        Assert.Equal(TimelineSourceKind.TextLog, source.Kind);
        Assert.Equal(3, source.EntryCount);
        Assert.Equal(2, source.SkippedCount);
        Assert.Equal(new long[] { 1, 3, 5 }, index.Entries.Select(e => e.Position));
        Assert.Equal(
            new[] { LogSeverity.Information, LogSeverity.Error, LogSeverity.Warning },
            index.Entries.Select(e => e.Severity));
        Assert.Equal(new DateTime(2026, 7, 23, 14, 12, 5).Ticks, index.Entries[1].Ticks);
    }

    [Fact]
    public async Task A_time_without_a_zone_is_marked_unzoned_and_one_with_a_zone_is_not()
    {
        var (log, loader) = TextLog(
            "mixed.log",
            "2026-07-23 14:12:00 INFO written locally",
            "2026-07-23T14:12:01Z INFO utc",
            "2026-07-23T16:12:02+02:00 INFO offset");

        var index = await Build(loader, log);

        Assert.Equal(new[] { true, false, false }, index.Entries.Select(e => e.Unzoned));
        Assert.Equal(1, index.Sources[0].UnzonedCount);
        Assert.True(index.Sources[0].HasUnzonedTimes);
        // The two zoned times are the same instant written in two zones, one second apart.
        Assert.Equal(new DateTime(2026, 7, 23, 14, 12, 2, DateTimeKind.Utc).Ticks - new DateTime(2026, 7, 23, 14, 12, 1, DateTimeKind.Utc).Ticks, index.Entries[2].Ticks - index.Entries[1].Ticks);
    }

    [Fact]
    public async Task Dates_that_cannot_be_read_without_guessing_are_not_given_a_time()
    {
        var (log, loader) = TextLog(
            "ambiguous.log",
            "04/05/2026 14:12:00 INFO slash dates could be either order",
            "2026-07-23 14:12:00 INFO fine");

        var index = await Build(loader, log);

        Assert.Single(index.Entries);
        Assert.Equal(2, index.Entries[0].Position);
        Assert.Equal(1, index.Sources[0].SkippedCount);
    }

    [Fact]
    public async Task Placeholder_and_absurd_years_are_not_events()
    {
        var (log, loader) = TextLog(
            "odd.log",
            "0001-01-01 00:00:00 INFO default value",
            "1601-01-01 00:00:00 INFO filetime zero",
            "9999-12-31 23:59:59 INFO never",
            "2026-07-23 14:12:00 INFO real");

        var index = await Build(loader, log);

        Assert.Equal(new long[] { 4 }, index.Entries.Select(e => e.Position));
    }

    [Fact]
    public async Task Event_logs_contribute_every_event_with_its_record_id_and_a_utc_time()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Ok(EventLog(artifact, new DataEventSource().Add("Service Control Manager", 7031).Add("Disk", 7).Add("Service Control Manager", 7034))));

        var index = await Build(loader, artifact);

        var source = Assert.Single(index.Sources);
        Assert.Equal(TimelineSourceKind.EventLog, source.Kind);
        Assert.Equal(3, source.EntryCount);
        Assert.Equal(new long[] { 1000, 1001, 1002 }, index.Entries.Select(e => e.Position));
        Assert.All(index.Entries, e => Assert.False(e.Unzoned));
        Assert.All(index.Entries, e => Assert.Equal(LogSeverity.Error, e.Severity));
        Assert.Equal(T0.Ticks, index.Entries[0].Ticks);
        Assert.Equal(T0.AddMinutes(2).Ticks, index.Entries[2].Ticks);
    }

    [Theory]
    [InlineData(EventLevels.Critical, LogSeverity.Error)]
    [InlineData(EventLevels.Error, LogSeverity.Error)]
    [InlineData(EventLevels.Warning, LogSeverity.Warning)]
    [InlineData(EventLevels.Information, LogSeverity.Information)]
    [InlineData(EventLevels.Verbose, LogSeverity.Debug)]
    [InlineData(EventLevels.LogAlways, LogSeverity.None)]
    public async Task Event_levels_map_to_severities(byte level, LogSeverity expected)
    {
        var artifact = Artifact("a.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Ok(EventLog(artifact, new LeveledEvents(level))));

        var index = await Build(loader, artifact);

        Assert.Equal(expected, index.Entries[0].Severity);
    }

    private sealed class LeveledEvents : IEventLogSource
    {
        private readonly byte _level;

        public LeveledEvents(byte level) => _level = level;

        public int Count => 1;
        public IReadOnlyList<EventLogProvider> Providers => Array.Empty<EventLogProvider>();
        public string ProviderName(ushort providerId) => "P";
        public EventSummary GetSummary(int index) => new(0, 5, T0, _level, 0, 1);
        public int FindByRecordId(long recordId) => 0;
        public EventDetail ReadDetail(int index) => throw new NotSupportedException();
        public string ReadSearchText(int index, bool includeMessage) => string.Empty;
    }

    [Fact]
    public async Task Only_event_logs_and_text_logs_are_read()
    {
        var log = Artifact("a.log", ArtifactType.TextLog);
        var cmd = Artifact("ipconfig.txt", ArtifactType.CommandOutput);
        var reg = Artifact("k.reg", ArtifactType.RegistryExport);
        var xml = Artifact("c.xml", ArtifactType.Xml);
        var container = Artifact("inner.zip", ArtifactType.TextLog) with { IsContainer = true };
        var loader = new FakeLoader();
        loader.Set(log, Ok(Text(log, "2026-07-23 14:12:00 INFO x")));

        var index = await Build(loader, log, cmd, reg, xml, container);

        Assert.Equal(new[] { log.Id }, loader.Loaded);
        Assert.Equal(1, index.ArtifactsExamined);
    }

    [Fact]
    public async Task Sources_are_numbered_in_provenance_order_and_silent_artifacts_are_left_out()
    {
        var b = Artifact("b.log", ArtifactType.TextLog);
        var a = Artifact("a.log", ArtifactType.TextLog);
        var empty = Artifact("c.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(b, Ok(Text(b, "2026-07-23 14:12:00 INFO b")));
        loader.Set(a, Ok(Text(a, "2026-07-23 14:12:00 INFO a")));
        loader.Set(empty, Ok(Text(empty, "nothing with a time", "still nothing")));

        var index = await Build(loader, b, a, empty);

        Assert.Equal(new[] { "a.log", "b.log" }, index.Sources.Select(s => s.Artifact.Name));
        Assert.Equal(new[] { 0, 1 }, index.Sources.Select(s => s.Index));
        Assert.Equal(new[] { 0, 1 }, index.Entries.Select(e => e.Source));
        Assert.Equal(3, index.ArtifactsExamined);
    }

    [Fact]
    public async Task An_artifact_that_cannot_be_read_is_reported_and_does_not_stop_the_others()
    {
        var bad = Artifact("bad.log", ArtifactType.TextLog);
        var good = Artifact("good.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Throw(bad, new IOException("disk gone"));
        loader.Set(good, Ok(Text(good, "2026-07-23 14:12:00 INFO ok")));

        var index = await Build(loader, bad, good);

        Assert.Single(index.Entries);
        Assert.Contains(index.Issues, i => i.Contains("bad.log") && i.Contains("disk gone"));
    }

    [Fact]
    public async Task A_document_that_fails_while_being_scanned_is_reported_too()
    {
        var artifact = Artifact("a.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Ok(EventLog(artifact, new ThrowingEvents())));

        var index = await Build(loader, artifact);

        Assert.Empty(index.Entries);
        Assert.Contains(index.Issues, i => i.Contains("a.evtx") && i.Contains("corrupt"));
    }

    private sealed class ThrowingEvents : IEventLogSource
    {
        public int Count => 3;
        public IReadOnlyList<EventLogProvider> Providers => Array.Empty<EventLogProvider>();
        public string ProviderName(ushort providerId) => "P";
        public EventSummary GetSummary(int index) => throw new InvalidDataException("corrupt chunk");
        public int FindByRecordId(long recordId) => -1;
        public EventDetail ReadDetail(int index) => throw new NotSupportedException();
        public string ReadSearchText(int index, bool includeMessage) => string.Empty;
    }

    [Fact]
    public async Task Unsupported_documents_contribute_nothing()
    {
        var artifact = Artifact("weird.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Ok(new UnsupportedDocument { Artifact = artifact, Reason = "no parser" }));

        var index = await Build(loader, artifact);

        Assert.Empty(index.Sources);
        Assert.Equal(1, index.ArtifactsExamined);
        Assert.Empty(index.Issues);
    }

    [Fact]
    public async Task Beyond_the_cap_later_sources_are_dropped_and_the_index_says_so()
    {
        var a = Artifact("a.log", ArtifactType.TextLog);
        var b = Artifact("b.log", ArtifactType.TextLog);
        var c = Artifact("c.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        string[] Lines(int n) => Enumerable.Range(0, n).Select(i => $"2026-07-23 14:12:{i:00} INFO line").ToArray();
        loader.Set(a, Ok(Text(a, Lines(4))));
        loader.Set(b, Ok(Text(b, Lines(4))));
        loader.Set(c, Ok(Text(c, Lines(4))));

        var index = await new TimelineService(loader, maxEntries: 6).BuildAsync(new[] { a, b, c }, null, CancellationToken.None);

        Assert.Equal(6, index.Count);
        Assert.Equal(6, index.Sources.Sum(s => s.EntryCount));
        Assert.Equal(new[] { 4, 2 }, index.Sources.Select(s => s.EntryCount));
        Assert.Equal(6, index.DroppedEntries);
        Assert.True(index.IsTruncated);
    }

    [Fact]
    public async Task Building_can_be_cancelled()
    {
        var (log, loader) = TextLog("a.log", "2026-07-23 14:12:00 INFO x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TimelineService(loader).BuildAsync(new[] { log }, null, cts.Token));
    }

    [Fact]
    public async Task Progress_reports_each_artifact_and_the_running_entry_count()
    {
        var a = Artifact("a.log", ArtifactType.TextLog);
        var b = Artifact("b.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(a, Ok(Text(a, "2026-07-23 14:12:00 INFO x", "2026-07-23 14:12:01 INFO y")));
        loader.Set(b, Ok(Text(b, "2026-07-23 14:12:00 INFO z")));
        var reports = new List<TimelineProgress>();
        var progress = new SyncProgress(reports.Add);

        await new TimelineService(loader).BuildAsync(new[] { a, b }, progress, CancellationToken.None);

        Assert.Equal(2, reports.Max(r => r.ArtifactsDone));
        Assert.Equal(3, reports.Max(r => r.EntriesSoFar));
        Assert.All(reports, r => Assert.Equal(2, r.ArtifactsTotal));
    }

    private sealed class SyncProgress : IProgress<TimelineProgress>
    {
        private readonly Action<TimelineProgress> _report;

        public SyncProgress(Action<TimelineProgress> report) => _report = report;

        public void Report(TimelineProgress value)
        {
            lock (this)
            {
                _report(value);
            }
        }
    }

    // ---- describing ----

    [Fact]
    public async Task A_text_entry_is_described_by_its_line()
    {
        var (log, loader) = TextLog("a.log", "2026-07-23 14:12:00 INFO first", "continuation", "   2026-07-23 14:12:09 ERROR   second one   ");
        var service = new TimelineService(loader);
        var index = await service.BuildAsync(new[] { log }, null, CancellationToken.None);

        Assert.Equal("2026-07-23 14:12:00 INFO first", await service.DescribeAsync(index, index.Entries[0], CancellationToken.None));
        Assert.Equal("2026-07-23 14:12:09 ERROR   second one", await service.DescribeAsync(index, index.Entries[1], CancellationToken.None));
    }

    [Fact]
    public async Task An_event_entry_is_described_by_provider_id_and_data()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Ok(EventLog(artifact, new DataEventSource().Add("Service Control Manager", 7031, "Spooler", "1"))));
        var service = new TimelineService(loader);
        var index = await service.BuildAsync(new[] { artifact }, null, CancellationToken.None);

        var text = await service.DescribeAsync(index, index.Entries[0], CancellationToken.None);

        Assert.Equal("Service Control Manager · 7031: param1=Spooler, param2=1", text);
    }

    [Fact]
    public async Task Describing_a_very_long_line_truncates_it()
    {
        var (log, loader) = TextLog("a.log", "2026-07-23 14:12:00 INFO " + new string('x', 5000));
        var service = new TimelineService(loader);
        var index = await service.BuildAsync(new[] { log }, null, CancellationToken.None);

        var text = await service.DescribeAsync(index, index.Entries[0], CancellationToken.None);

        Assert.True(text.Length < 700);
        Assert.EndsWith("…", text);
    }

    // ---- end to end ----

    [Fact]
    public async Task Events_and_log_lines_interleave_by_time()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var evtx = Artifact("System.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        // The event log has events at 14:12:00, 14:13:00 and 14:14:00 UTC.
        loader.Set(evtx, Ok(EventLog(evtx, new DataEventSource().Add("A", 1).Add("A", 2).Add("A", 3))));
        loader.Set(log, Ok(Text(
            log,
            "2026-07-23T14:12:30Z INFO between first and second",
            "2026-07-23T14:13:30Z ERROR between second and third")));
        var index = await Build(loader, log, evtx);

        var order = TimelineSelector.Select(index, new TimelineFilter());

        var where = order.Select(i => index.Sources[index.Entries[i].Source].Artifact.Name + ":" + index.Entries[i].Position);
        Assert.Equal(new[] { "System.evtx:1000", "agent.log:1", "System.evtx:1001", "agent.log:2", "System.evtx:1002" }, where);
        Assert.Equal(
            DiagnosticLocation.ForLine(log.Id, 2),
            index.LocationOf(index.Entries[order[3]]));
    }
}
