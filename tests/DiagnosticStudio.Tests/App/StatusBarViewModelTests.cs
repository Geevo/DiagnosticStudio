using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.Search;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Core.Timeline;
using DiagnosticStudio.Search;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public sealed class StatusBarViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-sb-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly DiagnosticArtifact _a = Artifact("a.log", ArtifactType.TextLog);
    private readonly DiagnosticArtifact _b = Artifact("b.log", ArtifactType.TextLog);

    public StatusBarViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A loader that does not finish until the test says so.</summary>
    private sealed class GateLoader : IDocumentLoader
    {
        private readonly Dictionary<Guid, TaskCompletionSource<DocumentLoadResult>> _gates = new();

        public Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(artifact.Id, out var gate))
                {
                    gate = new TaskCompletionSource<DocumentLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates[artifact.Id] = gate;
                }

                return gate.Task;
            }
        }

        public void Finish(DiagnosticArtifact artifact)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(artifact.Id, out var gate))
                {
                    gate = new TaskCompletionSource<DocumentLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates[artifact.Id] = gate;
                }

                gate.TrySetResult(new DocumentLoadResult(Text(artifact, "line"), null));
            }
        }
    }

    private sealed class GateIngestor : IBundleIngestor
    {
        public TaskCompletionSource<InvestigationWorkspace> Gate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset() => Gate = new TaskCompletionSource<InvestigationWorkspace>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Gate.Task;
    }

    private sealed class GateFindings : IFindingsService
    {
        public TaskCompletionSource<FindingsResult> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FindingsResult> EvaluateAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<FindingsProgress>? progress, CancellationToken cancellationToken) =>
            Gate.Task;
    }

    private sealed class GateSearch : IGlobalSearchService
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<SearchUpdate> SearchAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts,
            SearchQuery query,
            SearchOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Gate.Task;
            yield break;
        }
    }

    private sealed class GateTimeline : ITimelineService
    {
        public TaskCompletionSource<TimelineIndex> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimelineIndex> BuildAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<TimelineProgress>? progress, CancellationToken cancellationToken) =>
            Gate.Task;

        public Task<string> DescribeAsync(TimelineIndex index, TimelineEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);
    }

    private sealed class Rig
    {
        public required WorkspaceViewModel Workspace { get; init; }
        public required DocumentHostViewModel Host { get; init; }
        public required ProblemsViewModel Problems { get; init; }
        public required SearchResultsViewModel Search { get; init; }
        public required StatusBarViewModel Status { get; init; }
        public required GateLoader Loader { get; init; }
        public required GateIngestor Ingestor { get; init; }
        public required GateFindings Findings { get; init; }
        public required GateSearch SearchService { get; init; }
        public required GateTimeline Timeline { get; init; }
    }

    private Rig Create()
    {
        var loader = new GateLoader();
        var ingestor = new GateIngestor();
        var findings = new GateFindings();
        var searchService = new GateSearch();
        var timeline = new GateTimeline();
        var workspace = new WorkspaceViewModel(ingestor, _output);
        var host = new DocumentHostViewModel(workspace, loader, new NavigationHistory(), _output, cache: null, timeline: timeline);
        var problems = new ProblemsViewModel(workspace, findings, host, _output);
        var search = new SearchResultsViewModel(workspace, searchService, host, _output);
        return new Rig
        {
            Workspace = workspace,
            Host = host,
            Problems = problems,
            Search = search,
            Status = new StatusBarViewModel(workspace, host, problems, search),
            Loader = loader,
            Ingestor = ingestor,
            Findings = findings,
            SearchService = searchService,
            Timeline = timeline,
        };
    }

    private InvestigationWorkspace Bundle() =>
        new(Guid.NewGuid(), _dir, _dir, new[] { _a, _b }, Array.Empty<IngestionIssue>());

    /// <summary>Opens a bundle through the real path and lets the background evaluation settle at its gate.</summary>
    private async Task OpenBundle(Rig rig, bool finishFindings = true)
    {
        rig.Ingestor.Gate.TrySetResult(Bundle());
        await rig.Workspace.OpenAsync(_dir);
        if (finishFindings)
        {
            rig.Findings.Gate.TrySetResult(new FindingsResult(Array.Empty<Finding>(), Array.Empty<RuleIssue>(), 2));
            await rig.Problems.PendingEvaluation;
        }
    }

    [Fact]
    public void An_idle_application_says_ready()
    {
        var rig = Create();

        Assert.Equal("Ready", rig.Status.Text);
        Assert.False(rig.Status.IsBusy);
    }

    [Fact]
    public async Task Opening_an_archive_shows_that_it_is_being_opened_and_then_goes_back_to_ready()
    {
        var rig = Create();

        var opening = rig.Workspace.OpenAsync(_dir);
        Assert.True(rig.Status.IsBusy);
        Assert.StartsWith("Opening archive", rig.Status.Text);

        rig.Ingestor.Gate.SetResult(Bundle());
        rig.Findings.Gate.SetResult(new FindingsResult(Array.Empty<Finding>(), Array.Empty<RuleIssue>(), 2));
        await opening;
        await rig.Problems.PendingEvaluation;

        Assert.Equal("Ready", rig.Status.Text);
        Assert.False(rig.Status.IsBusy);
    }

    [Fact]
    public async Task A_file_that_is_loading_is_named()
    {
        var rig = Create();
        await OpenBundle(rig);

        rig.Host.OpenArtifact(_a);

        Assert.True(rig.Status.IsBusy);
        Assert.Equal("Loading a.log...", rig.Status.Text);

        rig.Loader.Finish(_a);
        await WaitFor(() => !rig.Status.IsBusy);
        Assert.Equal("Ready", rig.Status.Text);
    }

    [Fact]
    public async Task Several_files_loading_are_counted()
    {
        var rig = Create();
        await OpenBundle(rig);

        rig.Host.OpenArtifact(_a);
        rig.Host.OpenArtifact(_b);

        Assert.Equal("Loading 2 files...", rig.Status.Text);

        rig.Loader.Finish(_a);
        await WaitFor(() => rig.Status.Text == "Loading b.log...");
        rig.Loader.Finish(_b);
        await WaitFor(() => rig.Status.Text == "Ready");
    }

    [Fact]
    public async Task Evaluating_the_rules_shows_while_it_runs()
    {
        var rig = Create();
        await OpenBundle(rig, finishFindings: false);

        Assert.True(rig.Status.IsBusy);
        Assert.StartsWith("Evaluating rules", rig.Status.Text);

        rig.Findings.Gate.SetResult(new FindingsResult(Array.Empty<Finding>(), Array.Empty<RuleIssue>(), 2));
        await rig.Problems.PendingEvaluation;

        Assert.Equal("Ready", rig.Status.Text);
    }

    [Fact]
    public async Task Building_the_timeline_shows_while_it_runs_even_when_its_tab_is_closed()
    {
        var rig = Create();
        await OpenBundle(rig);

        var timeline = rig.Host.ShowTimeline()!;
        Assert.Equal("Building timeline...", rig.Status.Text);

        rig.Host.CloseCommand.Execute(timeline);
        Assert.Equal("Building timeline...", rig.Status.Text);

        rig.Timeline.Gate.SetResult(TimelineIndex.Empty);
        await timeline.PendingBuild;
        Assert.Equal("Ready", rig.Status.Text);
    }

    [Fact]
    public async Task A_search_shows_how_far_it_has_got()
    {
        var rig = Create();
        await OpenBundle(rig);

        rig.Search.Query = "boom";
        rig.Search.SearchCommand.Execute(null);

        Assert.True(rig.Status.IsBusy);
        Assert.Equal("Searching 0 of 2 files...", rig.Status.Text);

        rig.SearchService.Gate.SetResult();
        await rig.Search.PendingSearch;
        Assert.Equal("Ready", rig.Status.Text);
    }

    [Fact]
    public async Task Things_that_run_at_the_same_time_are_all_listed()
    {
        var rig = Create();
        await OpenBundle(rig, finishFindings: false);
        rig.Host.OpenArtifact(_a);

        Assert.Equal("Loading a.log...  ·  " + rig.Problems.StatusText, rig.Status.Text);
    }

    [Fact]
    public async Task Opening_an_archive_takes_over_the_line_while_other_work_is_dropped()
    {
        var rig = Create();
        await OpenBundle(rig, finishFindings: false);
        Assert.StartsWith("Evaluating", rig.Status.Text);

        rig.Ingestor.Reset();
        var opening = rig.Workspace.OpenAsync(_dir);

        Assert.StartsWith("Opening archive", rig.Status.Text);
        Assert.DoesNotContain("Evaluating", rig.Status.Text);

        rig.Ingestor.Gate.SetResult(Bundle());
        await opening;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the status did not change as expected");
    }
}
