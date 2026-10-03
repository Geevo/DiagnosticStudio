using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.Timeline;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Timeline;
using DiagnosticStudio.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public class TimelineViewModelTests
{
    private readonly OutputViewModel _output = new();

    // agent.log (zone unknown):          events in System.evtx (UTC):
    //   1  14:12:30 INFO  start            14:12:00  record 1000
    //   2  14:13:30 ERROR boom             14:13:00  record 1001
    //   3  (no time) trace                 14:14:00  record 1002
    //   4  14:20:00 WARN  slow
    private readonly DiagnosticArtifact _log = Artifact("agent.log", ArtifactType.TextLog);
    private readonly DiagnosticArtifact _evtx = Artifact("System.evtx", ArtifactType.EventLog);
    private readonly DiagnosticArtifact _plain = Artifact("notes.log", ArtifactType.TextLog);

    private (FakeLoader Loader, TimelineService Service) Services()
    {
        var loader = new FakeLoader();
        loader.Set(_log, new DocumentLoadResult(
            Text(_log, "2026-07-23 14:12:30 INFO start", "2026-07-23 14:13:30 ERROR boom", "    at Foo.Bar()", "2026-07-23 14:20:00 WARN slow"),
            null));
        loader.Set(_evtx, new DocumentLoadResult(
            EventLog(_evtx, new DataEventSource().Add("Service Control Manager", 7031, "Spooler").Add("Disk", 7).Add("Disk", 11)),
            null));
        loader.Set(_plain, new DocumentLoadResult(Text(_plain, "no times here", "none here either"), null));
        return (loader, new TimelineService(loader));
    }

    private async Task<TimelineDocumentViewModel> Create(
        Action<DiagnosticLocation>? open = null, params DiagnosticArtifact[] artifacts)
    {
        var (_, service) = Services();
        var vm = new TimelineDocumentViewModel(
            artifacts.Length == 0 ? new[] { _log, _evtx } : artifacts,
            service,
            open ?? (_ => { }),
            _output);
        await vm.PendingBuild;
        return vm;
    }

    private static string[] Times(TimelineDocumentViewModel vm) => vm.Rows.Select(r => r.TimeText).ToArray();

    private static string[] Where(TimelineDocumentViewModel vm) => vm.Rows.Select(r => r.SourceName + ":" + r.Entry.Position).ToArray();

    // ---- building and rows ----

    [Fact]
    public async Task The_timeline_lists_every_timestamped_entry_in_time_order()
    {
        var vm = await Create();

        Assert.False(vm.IsBuilding);
        Assert.Equal(
            new[] { "System.evtx:1000", "agent.log:1", "System.evtx:1001", "agent.log:2", "System.evtx:1002", "agent.log:4" },
            Where(vm));
        Assert.Equal(
            "6 of 6 entries from 2 logs · 2026-07-23 14:12:00 to 2026-07-23 14:20:00 UTC",
            vm.StatusText);
        Assert.Equal(new[] { "agent.log", "System.evtx" }, vm.Sources.Select(s => s.Name));
        Assert.Equal("Timeline", vm.Title);
        Assert.True(vm.CanClose);
    }

    [Fact]
    public async Task Rows_show_utc_times_and_mark_the_ones_written_without_a_zone()
    {
        var vm = await Create();

        Assert.Equal("2026-07-23 14:12:00.000", vm.Rows[0].TimeText);
        Assert.False(vm.Rows[0].IsUnzoned);
        Assert.Equal("~2026-07-23 14:12:30.000", vm.Rows[1].TimeText);
        Assert.True(vm.Rows[1].IsUnzoned);
        Assert.Equal("event 1000", vm.Rows[0].PositionText);
        Assert.Equal("line 1", vm.Rows[1].PositionText);
        Assert.Equal("Error", vm.Rows[0].LevelName);
        Assert.Equal("Information", vm.Rows[1].LevelName);
        Assert.Equal("Error", vm.Rows[3].Severity);
        Assert.Equal(string.Empty, vm.Rows[1].Severity);
    }

    [Fact]
    public async Task A_rows_text_is_read_from_its_source_when_first_needed()
    {
        var vm = await Create();
        var row = vm.Rows[1];

        Assert.Equal(string.Empty, row.Text); // asking starts the read
        await row.Loaded;

        Assert.Equal("2026-07-23 14:12:30 INFO start", row.Text);
        await vm.Rows[0].Loaded;
        Assert.Equal("Service Control Manager · 7031: param1=Spooler", vm.Rows[0].Text);
    }

    [Fact]
    public async Task A_rows_text_announces_itself_when_it_arrives()
    {
        var vm = await Create();
        var row = vm.Rows[1];
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _ = row.Text;
        await row.Loaded;

        Assert.Contains(nameof(TimelineRowViewModel.Text), changed);
    }

    [Fact]
    public async Task A_row_whose_source_cannot_be_read_says_so_instead_of_failing()
    {
        var vm = await Create();
        var other = new FakeLoader();
        var row = new TimelineRowViewModel(vm.Index, 0, 0, new TimelineService(other), null, this);

        await row.Loaded;

        Assert.StartsWith("(could not be read:", row.Text);
    }

    [Fact]
    public async Task Rows_link_to_the_line_or_event_they_came_from()
    {
        var vm = await Create();

        Assert.Equal(DiagnosticLocation.ForEventRecord(_evtx.Id, 1000), vm.Rows[0].Location);
        Assert.Equal(DiagnosticLocation.ForLine(_log.Id, 1), vm.Rows[1].Location);
    }

    [Fact]
    public async Task Opening_the_selected_row_opens_its_source()
    {
        var opened = new List<DiagnosticLocation>();
        var vm = await Create(opened.Add);

        vm.OpenSelectedCommand.Execute(null); // nothing selected yet
        vm.SelectedRow = vm.Rows[3];
        vm.OpenSelectedCommand.Execute(null);

        Assert.Equal(new[] { DiagnosticLocation.ForLine(_log.Id, 2) }, opened);
    }

    [Fact]
    public async Task Notices_explain_unzoned_times_and_lines_left_out()
    {
        var vm = await Create(null, _log, _evtx, _plain);

        Assert.True(vm.HasNotices);
        Assert.Contains("marked ~", vm.Notices);
        Assert.Contains("1 of 3 logs had no timestamps", vm.Notices);
        Assert.Contains("1 lines without a timestamp", vm.Notices);
    }

    [Fact]
    public async Task A_bundle_of_zoned_complete_logs_has_nothing_to_warn_about()
    {
        var evtxOnly = await Create(null, _evtx);

        Assert.False(evtxOnly.HasNotices);
        Assert.False(evtxOnly.Sources[0].HasUnzonedTimes);
    }

    [Fact]
    public async Task Logs_that_could_not_be_read_are_reported_in_output()
    {
        var loader = new FakeLoader();
        loader.Throw(_log, new IOException("locked"));
        loader.Set(_evtx, new DocumentLoadResult(EventLog(_evtx, new DataEventSource().Add("A", 1)), null));
        var vm = new TimelineDocumentViewModel(new[] { _log, _evtx }, new TimelineService(loader), _ => { }, _output);

        await vm.PendingBuild;

        Assert.Single(vm.Rows);
        Assert.Contains(_output.Entries, e => e.Severity == OutputSeverity.Warning && e.Source == "Timeline" && e.Message.Contains("locked"));
    }

    [Fact]
    public async Task A_bundle_with_no_logs_says_so()
    {
        var vm = await Create(null, Artifact("x.reg", ArtifactType.RegistryExport));

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasSources);
    }

    // ---- filters ----

    [Fact]
    public async Task A_severity_filter_narrows_the_list()
    {
        var vm = await Create();

        vm.SelectedSeverity = SeverityOption.All.Single(o => o.Filter == TimelineSeverityFilter.ErrorsOnly);
        await vm.PendingSelection;

        Assert.Equal(new[] { "System.evtx:1000", "System.evtx:1001", "agent.log:2", "System.evtx:1002" }, Where(vm).Take(4));
        Assert.Equal(4, vm.Rows.Count);
        Assert.StartsWith("4 of 6 entries", vm.StatusText);
    }

    [Fact]
    public async Task Unticking_a_log_removes_its_entries()
    {
        var vm = await Create();

        vm.Sources.Single(s => s.Name == "agent.log").IsIncluded = false;
        await vm.PendingSelection;

        Assert.All(vm.Rows, r => Assert.Equal("System.evtx", r.SourceName));
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task Setting_a_logs_zone_moves_its_times_and_reorders_the_list()
    {
        var vm = await Create();
        Assert.Equal("System.evtx:1000", Where(vm)[0]);

        // agent.log is written in UTC+02:00, so 14:12:30 there is 12:12:30 UTC, well before the events.
        vm.Sources.Single(s => s.Name == "agent.log").Offset = UtcOffsetOption.All.Single(o => o.Label == "UTC+02:00");
        await vm.PendingSelection;

        Assert.Equal(
            new[] { "agent.log:1", "agent.log:2", "agent.log:4", "System.evtx:1000", "System.evtx:1001", "System.evtx:1002" },
            Where(vm));
        Assert.Equal("~2026-07-23 12:12:30.000", vm.Rows[0].TimeText);
        Assert.EndsWith("2026-07-23 12:12:30 to 2026-07-23 14:14:00 UTC", vm.StatusText);
    }

    [Fact]
    public async Task The_zone_choice_only_appears_for_logs_with_unzoned_times()
    {
        var vm = await Create();

        Assert.True(vm.Sources.Single(s => s.Name == "agent.log").HasUnzonedTimes);
        Assert.False(vm.Sources.Single(s => s.Name == "System.evtx").HasUnzonedTimes);
    }

    [Fact]
    public async Task A_time_range_narrows_the_list_and_bad_input_is_reported_without_changing_it()
    {
        var vm = await Create();

        vm.FromText = "2026-07-23 14:13:00";
        vm.ToText = "2026-07-23 14:14:00";
        await vm.PendingSelection;
        Assert.Equal(new[] { "System.evtx:1001", "agent.log:2", "System.evtx:1002" }, Where(vm));
        Assert.Equal(string.Empty, vm.RangeError);

        vm.ToText = "not a time";
        await vm.PendingSelection;
        Assert.NotEqual(string.Empty, vm.RangeError);
        Assert.Equal(3, vm.Rows.Count);

        vm.ToText = "2026-07-23 14:00:00"; // before the start
        await vm.PendingSelection;
        Assert.Contains("start is after the end", vm.RangeError);
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task Around_selection_shows_what_else_happened_close_to_the_entry()
    {
        var vm = await Create();
        vm.SelectedRow = vm.Rows[3]; // agent.log line 2, 14:13:30
        vm.SelectedAround = AroundOption.All.Single(o => o.Window == TimeSpan.FromMinutes(1));

        vm.ShowAroundCommand.Execute(null);
        await vm.PendingSelection;

        Assert.Equal("2026-07-23 14:12:30", vm.FromText);
        Assert.Equal("2026-07-23 14:14:30", vm.ToText);
        Assert.Equal(new[] { "agent.log:1", "System.evtx:1001", "agent.log:2", "System.evtx:1002" }, Where(vm));
        Assert.Equal(2, vm.SelectedRow!.Entry.Position); // the entry stays selected
        Assert.Equal("agent.log", vm.SelectedRow.SourceName);
    }

    [Fact]
    public async Task Around_selection_with_nothing_selected_does_nothing()
    {
        var vm = await Create();

        vm.ShowAroundCommand.Execute(null);
        await vm.PendingSelection;

        Assert.Equal(string.Empty, vm.FromText);
        Assert.Equal(6, vm.Rows.Count);
    }

    [Fact]
    public async Task Clearing_the_range_or_all_filters_restores_the_list()
    {
        var vm = await Create();
        vm.Sources.Single(s => s.Name == "agent.log").IsIncluded = false;
        vm.SelectedSeverity = SeverityOption.All[3];
        vm.FromText = "2026-07-23 14:13:00";
        await vm.PendingSelection;

        vm.ClearRangeCommand.Execute(null);
        await vm.PendingSelection;
        Assert.Equal(string.Empty, vm.FromText);
        Assert.Equal(3, vm.Rows.Count); // still filtered by source and level

        vm.ClearFiltersCommand.Execute(null);
        await vm.PendingSelection;
        Assert.Equal(6, vm.Rows.Count);
        Assert.All(vm.Sources, s => Assert.True(s.IsIncluded));
        Assert.Equal(TimelineSeverityFilter.All, vm.SelectedSeverity.Filter);
    }

    [Fact]
    public async Task The_selection_survives_a_filter_change_when_the_entry_is_still_shown()
    {
        var vm = await Create();
        vm.SelectedRow = vm.Rows[3]; // agent.log:2 (error)
        var scrolled = new List<int>();
        vm.ScrollRequested += (_, row) => scrolled.Add(row);

        vm.SelectedSeverity = SeverityOption.All[3]; // errors only
        await vm.PendingSelection;

        Assert.Equal(2, vm.SelectedRow!.Entry.Position);
        Assert.Equal("agent.log", vm.SelectedRow.SourceName);
        Assert.Single(scrolled);
    }

    [Fact]
    public async Task Jumping_to_a_time_selects_the_first_entry_at_or_after_it()
    {
        var vm = await Create();
        var scrolled = new List<int>();
        vm.ScrollRequested += (_, row) => scrolled.Add(row);

        vm.JumpText = "2026-07-23 14:13:10";
        vm.JumpToTimeCommand.Execute(null);

        Assert.Equal("agent.log", vm.SelectedRow!.SourceName);
        Assert.Equal(2, vm.SelectedRow.Entry.Position);
        Assert.Equal(new[] { 3 }, scrolled);

        vm.JumpText = "2030-01-01"; // after everything: the last entry
        vm.JumpToTimeCommand.Execute(null);
        Assert.Equal(4, vm.SelectedRow.Entry.Position);

        vm.JumpText = "garbage";
        vm.JumpToTimeCommand.Execute(null); // ignored
        Assert.Equal(4, vm.SelectedRow.Entry.Position);
    }

    // ---- links in ----

    [Fact]
    public async Task Showing_a_location_selects_its_entry()
    {
        var vm = await Create();

        var found = await vm.ShowLocationAsync(DiagnosticLocation.ForEventRecord(_evtx.Id, 1001));

        Assert.True(found);
        Assert.Equal("System.evtx", vm.SelectedRow!.SourceName);
        Assert.Equal(1001, vm.SelectedRow.Entry.Position);
    }

    [Fact]
    public async Task A_line_without_a_time_selects_the_timestamped_line_above_it()
    {
        var vm = await Create();

        var found = await vm.ShowLocationAsync(DiagnosticLocation.ForLine(_log.Id, 3)); // the stack-trace line

        Assert.True(found);
        Assert.Equal(2, vm.SelectedRow!.Entry.Position);
    }

    [Fact]
    public async Task Showing_a_location_a_filter_hides_clears_the_filters_first()
    {
        var vm = await Create();
        vm.SelectedSeverity = SeverityOption.All[3];
        await vm.PendingSelection;
        Assert.Equal(4, vm.Rows.Count);

        var found = await vm.ShowLocationAsync(DiagnosticLocation.ForLine(_log.Id, 4)); // a warning

        Assert.True(found);
        Assert.Equal(TimelineSeverityFilter.All, vm.SelectedSeverity.Filter);
        Assert.Equal(4, vm.SelectedRow!.Entry.Position);
        Assert.Equal(6, vm.Rows.Count);
    }

    [Fact]
    public async Task A_place_with_no_timestamp_is_not_found()
    {
        var vm = await Create(null, _log, _evtx, _plain);

        Assert.False(await vm.ShowLocationAsync(DiagnosticLocation.ForLine(_plain.Id, 1)));
        Assert.False(await vm.ShowLocationAsync(DiagnosticLocation.ForLine(Guid.NewGuid(), 1)));
        Assert.False(await vm.ShowLocationAsync(DiagnosticLocation.ForArtifact(_log.Id)));
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public async Task Showing_a_location_waits_for_the_build()
    {
        var (_, service) = Services();
        var vm = new TimelineDocumentViewModel(new[] { _log, _evtx }, service, _ => { }, _output);

        var found = await vm.ShowLocationAsync(DiagnosticLocation.ForEventRecord(_evtx.Id, 1002));

        Assert.True(found);
        Assert.Equal(1002, vm.SelectedRow!.Entry.Position);
    }

    // ---- failure and cancellation ----

    private sealed class ScriptedService : ITimelineService
    {
        private readonly Func<CancellationToken, Task<TimelineIndex>> _build;

        public ScriptedService(Func<CancellationToken, Task<TimelineIndex>> build) => _build = build;

        public Task<TimelineIndex> BuildAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<TimelineProgress>? progress, CancellationToken cancellationToken) =>
            _build(cancellationToken);

        public Task<string> DescribeAsync(TimelineIndex index, TimelineEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);
    }

    [Fact]
    public async Task A_build_that_fails_is_reported_and_stops_the_busy_indicator()
    {
        var service = new ScriptedService(_ => throw new InvalidOperationException("index exploded"));
        var vm = new TimelineDocumentViewModel(new[] { _log }, service, _ => { }, _output);

        await vm.PendingBuild;

        Assert.False(vm.IsBuilding);
        Assert.Contains("index exploded", vm.StatusText);
        Assert.Contains(_output.Entries, e => e.Severity == OutputSeverity.Error && e.Message.Contains("index exploded"));
    }

    [Fact]
    public async Task Closing_the_bundle_cancels_a_build_in_progress()
    {
        var started = new TaskCompletionSource();
        var service = new ScriptedService(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return TimelineIndex.Empty;
        });
        var vm = new TimelineDocumentViewModel(new[] { _log }, service, _ => { }, _output);
        await started.Task;

        vm.Cancel();
        await vm.PendingBuild;

        Assert.Empty(vm.Sources);
        Assert.Empty(vm.Rows);
    }
}
