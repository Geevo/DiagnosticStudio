using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Rules;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

/// <summary>Files that failed to parse, were only partly read, have no viewer or are empty, listed beside the findings.</summary>
public sealed class ProblemsFilesViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-pf-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly Ingestor _ingestor = new();

    public ProblemsFilesViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Ingestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private sealed class FixedFindings : IFindingsService
    {
        public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();

        public Task<FindingsResult> EvaluateAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<FindingsProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new FindingsResult(Findings, Array.Empty<RuleIssue>(), artifacts.Count));
    }

    private sealed class ScriptedHealth : IFileHealthService
    {
        private readonly Func<IReadOnlyList<DiagnosticArtifact>, IProgress<FileHealthProgress>?, CancellationToken, Task<IReadOnlyList<FileProblem>>> _script;

        public ScriptedHealth(Func<IReadOnlyList<DiagnosticArtifact>, IProgress<FileHealthProgress>?, CancellationToken, Task<IReadOnlyList<FileProblem>>> script) =>
            _script = script;

        public Task<IReadOnlyList<FileProblem>> CheckAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<FileHealthProgress>? progress, CancellationToken cancellationToken) =>
            _script(artifacts, progress, cancellationToken);
    }

    private readonly DiagnosticArtifact _a = Artifact("a.xml", ArtifactType.Xml);
    private readonly DiagnosticArtifact _b = Artifact("k.reg", ArtifactType.RegistryExport);
    private readonly DiagnosticArtifact _c = Artifact("t.etl", ArtifactType.Trace);
    private readonly DiagnosticArtifact _d = Artifact("empty.log", ArtifactType.TextLog);

    private FileProblem P(DiagnosticArtifact artifact, FileProblemKind kind, string message = "msg", DiagnosticLocation? at = null) =>
        new(artifact, kind, message, "try this", at ?? DiagnosticLocation.ForArtifact(artifact.Id));

    private async Task<(ProblemsViewModel Vm, DocumentHostViewModel Host, WorkspaceViewModel Workspace)> Create(
        IFileHealthService? health, IFindingsService? findings = null)
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, new FakeLoader(), new NavigationHistory(), _output);
        var vm = new ProblemsViewModel(workspace, findings ?? new FixedFindings(), host, _output, health);
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _a, _b, _c, _d }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
        return (vm, host, workspace);
    }

    private static ScriptedHealth Returns(params FileProblem[] problems) =>
        new((_, _, _) => Task.FromResult<IReadOnlyList<FileProblem>>(problems));

    // ---- the Errors / Warnings / Messages buttons ----

    [Fact]
    public async Task The_buttons_count_files_by_severity_and_all_start_on()
    {
        var (vm, _, _) = await Create(Returns(
            P(_a, FileProblemKind.Failed),
            P(_b, FileProblemKind.Partial),
            P(_c, FileProblemKind.Caution),
            P(_d, FileProblemKind.Empty)));

        await vm.PendingEvaluation;

        Assert.True(vm.ShowErrors && vm.ShowWarnings && vm.ShowMessages);
        Assert.Equal("1 Error", vm.ErrorsLabel);
        Assert.Equal("1 Warning", vm.WarningsLabel);
        Assert.Equal("2 Messages", vm.MessagesLabel);
        Assert.Equal(4, vm.Items.Count);
    }

    [Fact]
    public async Task Turning_a_button_off_hides_that_severity_and_on_brings_it_back_in_order()
    {
        var (vm, _, _) = await Create(Returns(
            P(_a, FileProblemKind.Failed),
            P(_b, FileProblemKind.Partial),
            P(_d, FileProblemKind.Empty)));
        await vm.PendingEvaluation;

        vm.ShowWarnings = false;
        Assert.Equal(new[] { "Files that could not be read (1)", "Empty files (1)" }, vm.Items.Cast<FileProblemGroupViewModel>().Select(g => g.Title));

        vm.ShowErrors = false;
        vm.ShowMessages = false;
        Assert.Empty(vm.Items);
        Assert.Equal("1 Error", vm.ErrorsLabel);   // the counts do not depend on what is shown

        vm.ShowErrors = vm.ShowWarnings = vm.ShowMessages = true;
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal("Files that could not be read (1)", ((FileProblemGroupViewModel)vm.Items[0]).Title);
    }

    [Fact]
    public async Task Findings_count_and_filter_with_the_files()
    {
        var finding = new Finding
        {
            Id = "r1",
            Title = "Crash",
            Description = "d",
            Severity = FindingSeverity.Error,
            Evidence = new[] { new FindingEvidence { Location = DiagnosticLocation.ForArtifact(_a.Id), Description = "e" } },
        };
        var (vm, _, _) = await Create(Returns(P(_b, FileProblemKind.Failed)), new FixedFindings { Findings = new[] { finding } });
        await vm.PendingEvaluation;

        Assert.Equal("2 Errors", vm.ErrorsLabel);
        Assert.Equal(2, vm.Items.Count);

        vm.ShowErrors = false;

        Assert.Empty(vm.Items);
        Assert.Equal("2 Errors", vm.ErrorsLabel);
    }

    [Fact]
    public async Task A_new_bundle_resets_the_counts()
    {
        var (vm, _, workspace) = await Create(Returns(P(_a, FileProblemKind.Failed)));
        await vm.PendingEvaluation;
        Assert.Equal("1 Error", vm.ErrorsLabel);

        workspace.Close();

        Assert.Equal("0 Errors", vm.ErrorsLabel);
        Assert.Equal("0 Messages", vm.MessagesLabel);
    }

    // ---- grouping ----

    [Fact]
    public async Task File_problems_are_grouped_by_kind_most_serious_first()
    {
        var (vm, _, _) = await Create(Returns(
            P(_d, FileProblemKind.Empty),
            P(_c, FileProblemKind.NoViewer),
            P(_b, FileProblemKind.Partial),
            P(_a, FileProblemKind.Failed)));

        await vm.PendingEvaluation;

        Assert.Equal(
            new[]
            {
                "Files that could not be read (1)",
                "Files only partly read (1)",
                "Files with no viewer yet (1)",
                "Empty files (1)",
            },
            vm.FileGroups.Select(g => g.Title));
    }

    [Fact]
    public async Task Only_the_groups_that_need_attention_start_open()
    {
        var (vm, _, _) = await Create(Returns(
            P(_a, FileProblemKind.Failed),
            P(_b, FileProblemKind.Partial),
            P(_c, FileProblemKind.NoViewer),
            P(_d, FileProblemKind.Empty)));

        await vm.PendingEvaluation;

        Assert.Equal(new[] { true, true, false, false }, vm.FileGroups.Select(g => g.IsExpanded));
    }

    [Fact]
    public async Task Each_file_shows_its_name_why_and_what_to_try()
    {
        var (vm, _, _) = await Create(Returns(P(_a, FileProblemKind.Failed, "Not well-formed XML")));

        await vm.PendingEvaluation;

        var item = vm.FileGroups[0].Items[0];
        Assert.Equal("a.xml", item.Title);
        Assert.Equal("Not well-formed XML", item.Message);
        Assert.Equal("try this", item.Remedy);
        Assert.Equal("Bundle.zip → a.xml", item.Where);
        Assert.Equal(FindingSeverity.Error, item.Severity);
    }

    [Theory]
    [InlineData(FileProblemKind.Failed, FindingSeverity.Error)]
    [InlineData(FileProblemKind.Partial, FindingSeverity.Warning)]
    [InlineData(FileProblemKind.NoViewer, FindingSeverity.Information)]
    [InlineData(FileProblemKind.Empty, FindingSeverity.Information)]
    public async Task The_severity_of_a_group_follows_its_kind(FileProblemKind kind, FindingSeverity expected)
    {
        var (vm, _, _) = await Create(Returns(P(_a, kind)));

        await vm.PendingEvaluation;

        Assert.Equal(expected, vm.FileGroups[0].Severity);
    }

    [Fact]
    public async Task The_panel_lists_findings_first_and_file_problems_after()
    {
        var finding = new Finding { Id = "f", Severity = FindingSeverity.Error, Title = "t", Description = "d" };
        var (vm, _, _) = await Create(Returns(P(_a, FileProblemKind.Failed)), new FixedFindings { Findings = new[] { finding } });

        await vm.PendingEvaluation;

        Assert.IsType<ProblemGroupViewModel>(vm.Items[0]);
        Assert.IsType<FileProblemGroupViewModel>(vm.Items[1]);
        Assert.Equal(2, vm.Items.Count);
    }

    // ---- counts and text ----

    [Fact]
    public async Task Unreadable_files_are_counted_apart_from_files_with_no_viewer_or_no_content()
    {
        var finding = new Finding { Id = "f", Severity = FindingSeverity.Error, Title = "t", Description = "d" };
        var health = Returns(P(_a, FileProblemKind.Failed), P(_b, FileProblemKind.Partial), P(_c, FileProblemKind.NoViewer));

        var (both, _, _) = await Create(health, new FixedFindings { Findings = new[] { finding } });
        await both.PendingEvaluation;
        Assert.Equal(2, both.UnreadableFileCount); // the file with no viewer is not an unreadable file

        var (filesOnly, _, _) = await Create(Returns(P(_a, FileProblemKind.Failed)));
        await filesOnly.PendingEvaluation;
        Assert.Equal(1, filesOnly.UnreadableFileCount);

        var (neither, _, _) = await Create(Returns(P(_c, FileProblemKind.NoViewer), P(_d, FileProblemKind.Empty)));
        await neither.PendingEvaluation;
        Assert.Equal(0, neither.UnreadableFileCount);
    }

    [Fact]
    public async Task A_bundle_whose_files_all_read_fine_adds_nothing()
    {
        var (vm, _, _) = await Create(Returns());

        await vm.PendingEvaluation;

        Assert.Empty(vm.FileGroups);
        Assert.DoesNotContain("Files:", vm.StatusText);
        Assert.Equal("Problems", vm.TabHeader);
    }

    [Fact]
    public async Task Without_a_file_check_the_panel_behaves_as_before()
    {
        var (vm, _, _) = await Create(health: null);

        await vm.PendingEvaluation;

        Assert.Empty(vm.FileGroups);
        Assert.False(vm.IsEvaluating);
    }

    // ---- while it runs ----

    [Fact]
    public async Task The_panel_stays_busy_and_reports_progress_while_files_are_checked()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<FileProblem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        var health = new ScriptedHealth((_, progress, _) =>
        {
            progress?.Report(new FileHealthProgress(3, 40));
            started.SetResult();
            return gate.Task;
        });
        var (vm, _, _) = await Create(health);
        await started.Task;
        for (var i = 0; i < 200 && !vm.StatusText.StartsWith("Checking files", StringComparison.Ordinal); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(vm.IsEvaluating);
        Assert.Equal("Checking files... 3 of 40", vm.StatusText);

        gate.SetResult(new[] { P(_a, FileProblemKind.Failed) });
        await vm.PendingEvaluation;

        Assert.False(vm.IsEvaluating);
        Assert.Single(vm.FileGroups);
    }

    [Fact]
    public async Task A_file_check_that_fails_is_reported_and_the_findings_stay()
    {
        var finding = new Finding { Id = "f", Severity = FindingSeverity.Warning, Title = "t", Description = "d" };
        var health = new ScriptedHealth((_, _, _) => throw new InvalidOperationException("scan exploded"));
        var (vm, _, _) = await Create(health, new FixedFindings { Findings = new[] { finding } });

        await vm.PendingEvaluation;

        Assert.False(vm.IsEvaluating);
        Assert.Equal(1, vm.FindingCount);
        Assert.Empty(vm.FileGroups);
        Assert.Contains(_output.Entries, e => e.Severity == OutputSeverity.Error && e.Message.Contains("scan exploded"));
    }

    [Fact]
    public async Task Opening_another_bundle_clears_the_file_problems_and_cancels_the_check()
    {
        var token = default(CancellationToken);
        var health = new ScriptedHealth(async (_, _, ct) =>
        {
            token = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<FileProblem>();
        });
        var (vm, _, workspace) = await Create(health);
        for (var i = 0; i < 200 && !token.CanBeCanceled; i++)
        {
            await Task.Delay(10);
        }

        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _a }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);

        Assert.True(token.IsCancellationRequested);
        Assert.Empty(vm.FileGroups);
        Assert.Empty(vm.Items);
    }

    // ---- opening ----

    [Fact]
    public async Task Activating_a_file_problem_opens_the_file_at_the_place_that_went_wrong()
    {
        var at = DiagnosticLocation.ForLine(_b.Id, 174);
        var (vm, host, _) = await Create(Returns(P(_b, FileProblemKind.Partial, at: at)));
        await vm.PendingEvaluation;

        vm.OpenFileProblemCommand.Execute(vm.FileGroups[0].Items[0]);

        var document = Assert.IsType<ArtifactDocumentViewModel>(host.ActiveDocument);
        Assert.Equal(_b.Id, document.Artifact.Id);
    }

    [Fact]
    public async Task A_file_problem_is_not_something_to_show_in_the_timeline()
    {
        var (vm, _, _) = await Create(Returns(P(_a, FileProblemKind.Failed)));
        await vm.PendingEvaluation;

        Assert.False(vm.ShowInTimelineCommand.CanExecute(vm.FileGroups[0].Items[0]));
        Assert.False(vm.ShowInTimelineCommand.CanExecute(vm.FileGroups[0]));
    }
}
