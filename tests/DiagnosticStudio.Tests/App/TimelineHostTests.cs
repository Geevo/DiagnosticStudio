using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.Timeline;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public sealed class TimelineHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-tlh-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly DiagnosticArtifact _log = Artifact("agent.log", ArtifactType.TextLog);
    private readonly DiagnosticArtifact _evtx = Artifact("System.evtx", ArtifactType.EventLog);
    private readonly FixedIngestor _ingestor = new();

    public TimelineHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FixedIngestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private (DocumentHostViewModel Host, WorkspaceViewModel Workspace, ExplorerViewModel Explorer) Create(bool withTimeline = true)
    {
        var loader = new FakeLoader();
        loader.Set(_log, new DocumentLoadResult(Text(_log, "2026-07-23 14:12:30 INFO start", "2026-07-23 14:13:30 ERROR boom"), null));
        loader.Set(_evtx, new DocumentLoadResult(EventLog(_evtx, new DataEventSource().Add("Disk", 7)), null));
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(
            workspace, loader, new NavigationHistory(), _output, cache: null, timeline: withTimeline ? new TimelineService(loader) : null);
        return (host, workspace, new ExplorerViewModel(workspace, host));
    }

    private async Task Open(WorkspaceViewModel workspace)
    {
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _log, _evtx }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
    }

    [Fact]
    public void Without_a_bundle_there_is_no_timeline()
    {
        var (host, _, explorer) = Create();

        Assert.Null(host.ShowTimeline());
        Assert.False(explorer.OpenTimelineCommand.CanExecute(null));
        Assert.DoesNotContain(host.Documents, d => d is TimelineDocumentViewModel);
    }

    [Fact]
    public async Task Opening_a_bundle_enables_the_timeline_command()
    {
        var (_, workspace, explorer) = Create();

        await Open(workspace);

        Assert.True(explorer.OpenTimelineCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_timeline_opens_as_a_tab_once_and_is_kept_when_the_tab_is_closed()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);

        var first = host.ShowTimeline();
        var again = host.ShowTimeline();

        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.Single(host.Documents.OfType<TimelineDocumentViewModel>());
        Assert.Same(first, host.ActiveDocument);

        host.CloseCommand.Execute(first);
        Assert.DoesNotContain(host.Documents, d => d is TimelineDocumentViewModel);

        var reopened = host.ShowTimeline();
        Assert.Same(first, reopened); // not rebuilt
        await first!.PendingBuild;
        Assert.Equal(3, first.Rows.Count);
    }

    [Fact]
    public async Task A_different_bundle_gets_its_own_timeline_and_the_old_one_is_stopped()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);
        var first = host.ShowTimeline()!;
        await first.PendingBuild;

        await Open(workspace);

        Assert.DoesNotContain(host.Documents, d => d is TimelineDocumentViewModel);
        var second = host.ShowTimeline();
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Without_a_timeline_service_nothing_opens()
    {
        var (host, workspace, _) = Create(withTimeline: false);
        await Open(workspace);

        Assert.Null(host.ShowTimeline());
    }

    [Fact]
    public async Task Show_in_timeline_selects_the_entry_for_a_location()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);

        var found = await host.ShowInTimelineAsync(DiagnosticLocation.ForLine(_log.Id, 2));

        Assert.True(found);
        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(2, timeline.SelectedRow!.Entry.Position);
        Assert.Equal("agent.log", timeline.SelectedRow.SourceName);
    }

    [Fact]
    public async Task Show_in_timeline_for_an_untimed_place_says_so_in_output()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);

        var found = await host.ShowInTimelineAsync(DiagnosticLocation.ForRegistry(_log.Id, "HKLM\\X"));

        Assert.False(found);
        Assert.Contains(_output.Entries, e => e.Source == "Timeline" && e.Message.Contains("no timestamp"));
    }

    [Fact]
    public async Task Show_in_timeline_without_a_bundle_is_false()
    {
        var (host, _, _) = Create();

        Assert.False(await host.ShowInTimelineAsync(DiagnosticLocation.ForLine(_log.Id, 1)));
    }

    [Fact]
    public async Task Back_after_opening_a_row_returns_to_the_timeline_not_to_an_earlier_file()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);
        host.OpenArtifact(_evtx);               // an earlier file the user looked at
        var timeline = host.ShowTimeline()!;
        await timeline.PendingBuild;
        timeline.SelectedRow = timeline.Rows.Single(r => r.SourceName == "agent.log" && r.Entry.Position == 2);

        timeline.OpenSelectedCommand.Execute(null);
        Assert.IsType<ArtifactDocumentViewModel>(host.ActiveDocument);

        host.GoBackCommand.Execute(null);

        Assert.Same(timeline, host.ActiveDocument);
        Assert.Equal(2, timeline.SelectedRow!.Entry.Position); // the row is still selected
    }

    [Fact]
    public async Task Back_and_forward_walk_between_the_timeline_and_the_files_opened_from_it()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);
        var timeline = host.ShowTimeline()!;
        await timeline.PendingBuild;
        timeline.SelectedRow = timeline.Rows.First(r => r.SourceName == "agent.log");
        timeline.OpenSelectedCommand.Execute(null);
        var file = host.ActiveDocument;

        host.GoBackCommand.Execute(null);
        Assert.Same(timeline, host.ActiveDocument);

        host.GoForwardCommand.Execute(null);
        Assert.Same(file, host.ActiveDocument);
        Assert.False(host.GoForwardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Going_back_to_a_timeline_whose_tab_was_closed_reopens_it_without_rebuilding()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);
        var timeline = host.ShowTimeline()!;
        await timeline.PendingBuild;
        timeline.SelectedRow = timeline.Rows.First(r => r.SourceName == "agent.log");
        timeline.OpenSelectedCommand.Execute(null);
        host.CloseCommand.Execute(timeline);

        host.GoBackCommand.Execute(null);

        Assert.Same(timeline, host.ActiveDocument);
        Assert.Contains(timeline, host.Documents);
    }

    [Fact]
    public void The_timeline_location_round_trips_through_its_text_form()
    {
        var location = DiagnosticLocation.ForTimeline(5);

        Assert.True(DiagnosticLocation.TryParse(location.ToString(), out var parsed));
        Assert.Equal(location, parsed);
        Assert.Equal(DiagnosticLocationKind.Timeline, parsed!.Kind);
    }

    [Fact]
    public async Task Opening_a_row_from_the_timeline_opens_the_artifact_at_that_place()
    {
        var (host, workspace, _) = Create();
        await Open(workspace);
        var timeline = host.ShowTimeline()!;
        await timeline.PendingBuild;

        timeline.SelectedRow = timeline.Rows.Single(r => r.SourceName == "agent.log" && r.Entry.Position == 2);
        timeline.OpenSelectedCommand.Execute(null);

        var document = Assert.IsType<ArtifactDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(_log.Id, document.Artifact.Id);
        Assert.Contains(timeline, host.Documents); // the timeline tab stays
    }
}
