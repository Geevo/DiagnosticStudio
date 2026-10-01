using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.EventLogViewer;
using DiagnosticStudio.App.ViewModels.Search;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.App.ViewModels.Timeline;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Search;
using DiagnosticStudio.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

/// <summary>The ways into the timeline from elsewhere: the selected line or event, findings and search hits.</summary>
public sealed class TimelineEntryPointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-tle-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly DiagnosticArtifact _log = Artifact("agent.log", ArtifactType.TextLog);
    private readonly DiagnosticArtifact _evtx = Artifact("System.evtx", ArtifactType.EventLog);
    private readonly Ingestor _ingestor = new();

    public TimelineEntryPointTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Ingestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private sealed class NoFindings : IFindingsService
    {
        public Task<FindingsResult> EvaluateAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<FindingsProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new FindingsResult(Array.Empty<Finding>(), Array.Empty<RuleIssue>(), 0));
    }

    private sealed class NoSearch : IGlobalSearchService
    {
        public IAsyncEnumerable<SearchUpdate> SearchAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, SearchQuery query, SearchOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private async Task<(DocumentHostViewModel Host, WorkspaceViewModel Workspace)> OpenBundle()
    {
        var loader = new FakeLoader();
        loader.Set(_log, new DocumentLoadResult(Text(_log, "2026-07-23 14:12:30 INFO start", "2026-07-23 14:13:30 ERROR boom", "    at Foo()"), null));
        loader.Set(_evtx, new DocumentLoadResult(EventLog(_evtx, new DataEventSource().Add("Disk", 7).Add("Disk", 11)), null));
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, loader, new NavigationHistory(), _output, cache: null, timeline: new TimelineService(loader));
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _log, _evtx }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
        return (host, workspace);
    }

    private static async Task<ArtifactDocumentViewModel> Loaded(DocumentHostViewModel host, DiagnosticArtifact artifact)
    {
        var document = host.Documents.OfType<ArtifactDocumentViewModel>().Single(d => d.Artifact.Id == artifact.Id);
        for (var i = 0; i < 200 && document.IsLoading; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(document.IsLoading);
        return document;
    }

    // ---- viewers know where they are ----

    [Fact]
    public void A_text_viewer_reports_its_current_line_as_a_location()
    {
        var viewer = new TextViewerViewModel(new ListLines(new[] { "a", "b", "c" }));
        var id = Guid.NewGuid();

        Assert.Null(viewer.CurrentPosition(id));

        viewer.SetCurrentLineFromSelection(3);

        Assert.Equal(DiagnosticLocation.ForLine(id, 3), viewer.CurrentPosition(id));
    }

    [Fact]
    public void An_event_viewer_reports_its_selected_event_as_a_location()
    {
        var viewer = new EventLogViewerViewModel(EventLog(_evtx, new DataEventSource().Add("Disk", 7).Add("Disk", 11)));

        Assert.Null(viewer.CurrentPosition(_evtx.Id));

        viewer.SelectedEvent = viewer.Events[1];

        Assert.Equal(DiagnosticLocation.ForEventRecord(_evtx.Id, 1001), viewer.CurrentPosition(_evtx.Id));
    }

    // ---- from the active document ----

    [Fact]
    public async Task Show_in_timeline_uses_the_selected_line_of_the_active_document()
    {
        var (host, _) = await OpenBundle();
        host.OpenArtifact(_log);
        var document = await Loaded(host, _log);
        ((TextViewerViewModel)document.Viewer!).SetCurrentLineFromSelection(2);

        await host.ShowActiveInTimelineAsync();

        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(2, timeline.SelectedRow!.Entry.Position);
        Assert.Equal("agent.log", timeline.SelectedRow.SourceName);
    }

    [Fact]
    public async Task Show_in_timeline_uses_the_selected_event_of_the_active_event_log()
    {
        var (host, _) = await OpenBundle();
        host.OpenArtifact(_evtx);
        var document = await Loaded(host, _evtx);
        var viewer = (EventLogViewerViewModel)document.Viewer!;
        viewer.SelectedEvent = viewer.Events[1];

        await host.ShowActiveInTimelineAsync();

        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(1001, timeline.SelectedRow!.Entry.Position);
    }

    [Fact]
    public async Task Show_in_timeline_with_nothing_selected_just_opens_the_timeline()
    {
        var (host, _) = await OpenBundle();
        host.OpenArtifact(_log);
        await Loaded(host, _log);

        await host.ShowActiveInTimelineAsync(); // no line selected

        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        await timeline.PendingBuild;
        Assert.Null(timeline.SelectedRow);
    }

    [Fact]
    public async Task Show_in_timeline_from_the_overview_just_opens_the_timeline()
    {
        var (host, _) = await OpenBundle();

        await host.ShowActiveInTimelineAsync();

        Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
    }

    // ---- from findings ----

    private ProblemsViewModel Problems(DocumentHostViewModel host, WorkspaceViewModel workspace) =>
        new(workspace, new NoFindings(), host, _output);

    private static FindingViewModel FindingWith(DiagnosticArtifact artifact, params int[] lines) => new(
        new Finding
        {
            Id = "f",
            Severity = FindingSeverity.Error,
            Title = "t",
            Description = "d",
            Evidence = lines.Select(n => new FindingEvidence { Location = DiagnosticLocation.ForLine(artifact.Id, n) }).ToList(),
        },
        _ => artifact.Name);

    [Fact]
    public async Task A_findings_evidence_can_be_shown_in_the_timeline()
    {
        var (host, workspace) = await OpenBundle();
        var problems = Problems(host, workspace);
        var finding = FindingWith(_log, 2, 1);

        var command = problems.ShowInTimelineCommand;
        Assert.True(command.CanExecute(finding.Evidence[0]));
        await command.ExecuteAsync(finding.Evidence[0]);

        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(2, timeline.SelectedRow!.Entry.Position);
    }

    [Fact]
    public async Task A_finding_shows_its_first_evidence()
    {
        var (host, workspace) = await OpenBundle();
        var problems = Problems(host, workspace);

        await problems.ShowInTimelineCommand.ExecuteAsync(FindingWith(_log, 1, 2));

        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(1, timeline.SelectedRow!.Entry.Position);
    }

    [Fact]
    public async Task Groups_and_findings_without_evidence_cannot_be_shown_in_the_timeline()
    {
        var (host, workspace) = await OpenBundle();
        var problems = Problems(host, workspace);

        Assert.False(problems.ShowInTimelineCommand.CanExecute(null));
        Assert.False(problems.ShowInTimelineCommand.CanExecute(FindingWith(_log)));
        Assert.False(problems.ShowInTimelineCommand.CanExecute(new ProblemGroupViewModel(FindingSeverity.Error, Array.Empty<FindingViewModel>())));
    }

    // ---- from search results ----

    [Fact]
    public async Task A_search_hit_can_be_shown_in_the_timeline_and_a_placeholder_cannot()
    {
        var (host, workspace) = await OpenBundle();
        var search = new SearchResultsViewModel(workspace, new NoSearch(), host, _output);
        var hit = new SearchHitViewModel(new SearchHit(_log.Id, DiagnosticLocation.ForLine(_log.Id, 2), "ERROR boom", 0, 5));
        var placeholder = new SearchHitViewModel("… 40 more not listed.");

        Assert.False(search.ShowHitInTimelineCommand.CanExecute(placeholder));
        Assert.False(search.ShowHitInTimelineCommand.CanExecute(null));
        Assert.True(search.ShowHitInTimelineCommand.CanExecute(hit));
        await search.ShowHitInTimelineCommand.ExecuteAsync(hit);

        var timeline = Assert.IsType<TimelineDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(2, timeline.SelectedRow!.Entry.Position);
    }
}
