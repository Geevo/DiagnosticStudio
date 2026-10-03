using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.App;

public sealed class ProblemsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-pvm-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();

    public ProblemsViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class ScriptedService : IFindingsService
    {
        private readonly Func<IReadOnlyList<DiagnosticArtifact>, CancellationToken, Task<FindingsResult>> _script;

        public ScriptedService(Func<IReadOnlyList<DiagnosticArtifact>, CancellationToken, Task<FindingsResult>> script) => _script = script;

        public int Calls { get; private set; }

        public Task<FindingsResult> EvaluateAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<FindingsProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return _script(artifacts, cancellationToken);
        }
    }

    private sealed class FixedIngestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private static DiagnosticArtifact Artifact(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OriginalPath = name,
        ExtractedPath = null,
        Provenance = new[] { "Bundle.zip", name },
        ArtifactType = ArtifactType.TextLog,
    };

    private static Finding Finding(DiagnosticArtifact a, string id, FindingSeverity severity, string title, params int[] lines) => new()
    {
        Id = id,
        Severity = severity,
        Title = title,
        Description = title + " description",
        Evidence = lines.Select(n => new FindingEvidence { Location = DiagnosticLocation.ForLine(a.Id, n), Description = "Line " + n }).ToList(),
    };

    private readonly FixedIngestor _ingestor = new();

    private async Task<(ProblemsViewModel Vm, WorkspaceViewModel Workspace, DocumentHostViewModel Host)> Create(
        ScriptedService service, params DiagnosticArtifact[] artifacts)
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(
            workspace,
            new DocumentLoader(new IDiagnosticParser[] { new TextLogParser(), new UnsupportedArtifactParser() }),
            new NavigationHistory(),
            _output);
        var vm = new ProblemsViewModel(workspace, service, host, _output);
        await Open(workspace, artifacts);
        return (vm, workspace, host);
    }

    // Goes through the real open path so WorkspaceChanged is raised exactly as in the application.
    private async Task Open(WorkspaceViewModel workspace, params DiagnosticArtifact[] artifacts)
    {
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, artifacts, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
    }

    [Fact]
    public async Task Findings_are_grouped_by_severity_with_counts_and_a_tab_header()
    {
        var a = Artifact("agent.log");
        var service = new ScriptedService((arts, _) => Task.FromResult(new FindingsResult(
            new[]
            {
                Finding(a, "1", FindingSeverity.Error, "Crash", 3),
                Finding(a, "2", FindingSeverity.Warning, "Repeated", 5, 6),
                Finding(a, "3", FindingSeverity.Warning, "Other", 9),
                Finding(a, "4", FindingSeverity.Information, "Note", 1),
            },
            Array.Empty<RuleIssue>(),
            1)));
        var (vm, _, _) = await Create(service, a);

        await vm.PendingEvaluation;

        Assert.Equal(new[] { "Errors (1)", "Warnings (2)", "Information (1)" }, vm.Groups.Select(g => g.Title));
        Assert.Equal(4, vm.FindingCount);
        Assert.Equal("Problems (4)", vm.TabHeader);
        Assert.StartsWith("4 findings: 1 errors, 2 warnings, 1 information", vm.StatusText);
        Assert.False(vm.IsEvaluating);
        Assert.Equal("agent.log", vm.Groups[0].Findings[0].ArtifactName);
    }

    [Fact]
    public async Task Attention_lists_only_errors_and_warnings_most_severe_first_and_is_capped()
    {
        var a = Artifact("agent.log");
        var findings = new List<Finding> { Finding(a, "e", FindingSeverity.Error, "E", 1) };
        findings.AddRange(Enumerable.Range(0, 8).Select(i => Finding(a, "w" + i, FindingSeverity.Warning, "W" + i, i + 1)));
        findings.Add(Finding(a, "i", FindingSeverity.Information, "I", 1));
        var service = new ScriptedService((_, _) => Task.FromResult(new FindingsResult(findings, Array.Empty<RuleIssue>(), 1)));
        var (vm, _, _) = await Create(service, a);

        await vm.PendingEvaluation;

        Assert.Equal(ProblemsViewModel.AttentionLimit, vm.Attention.Count);
        Assert.Equal("E", vm.Attention[0].Title);
        Assert.All(vm.Attention, f => Assert.True(f.Severity >= FindingSeverity.Warning));
        Assert.True(vm.HasAttention);
    }

    [Fact]
    public async Task No_findings_says_so_without_claiming_the_bundle_is_healthy()
    {
        var a = Artifact("agent.log");
        var service = new ScriptedService((_, _) => Task.FromResult(new FindingsResult(Array.Empty<Finding>(), Array.Empty<RuleIssue>(), 3)));
        var (vm, _, _) = await Create(service, a);

        await vm.PendingEvaluation;

        Assert.Empty(vm.Groups);
        Assert.Equal("Problems", vm.TabHeader);
        Assert.Contains("No findings", vm.StatusText);
        Assert.Contains("does not show that nothing is wrong", vm.StatusText);
        Assert.False(vm.HasFindings);
    }

    [Fact]
    public async Task Rule_issues_go_to_the_output_panel()
    {
        var a = Artifact("agent.log");
        var service = new ScriptedService((_, _) => Task.FromResult(new FindingsResult(
            Array.Empty<Finding>(), new[] { new RuleIssue("r", "Bundle.zip → agent.log", "Rule failed: boom") }, 1)));
        var (vm, _, _) = await Create(service, a);

        await vm.PendingEvaluation;

        Assert.Contains(_output.Entries, e => e.Source == "Rules" && e.Message.Contains("Rule failed: boom"));
    }

    [Fact]
    public async Task Opening_evidence_or_a_finding_navigates_to_the_source()
    {
        var path = Path.Combine(_dir, "agent.log");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var a = Artifact("agent.log") with { ExtractedPath = path };
        var service = new ScriptedService((_, _) => Task.FromResult(new FindingsResult(
            new[] { Finding(a, "1", FindingSeverity.Warning, "Repeated", 2, 3) }, Array.Empty<RuleIssue>(), 1)));
        var (vm, _, host) = await Create(service, a);
        await vm.PendingEvaluation;
        var finding = vm.Groups[0].Findings[0];

        vm.OpenFindingCommand.Execute(finding);

        Assert.Equal(a.Id, host.ActiveDocument?.Location?.ArtifactId);
        Assert.Equal(2, finding.Evidence[0].Location.NumericPosition);

        vm.OpenEvidenceCommand.Execute(finding.Evidence[1]);
        Assert.Equal(a.Id, host.ActiveDocument?.Location?.ArtifactId);
    }

    [Fact]
    public async Task Opening_a_new_bundle_replaces_the_findings_and_ignores_the_superseded_run()
    {
        var a = Artifact("a.log");
        var b = Artifact("b.log");
        var first = new TaskCompletionSource<FindingsResult>();
        var service = new ScriptedService((arts, token) =>
            arts[0].Name == "a.log" ? first.Task : Task.FromResult(new FindingsResult(
                new[] { Finding(b, "b1", FindingSeverity.Error, "From b", 1) }, Array.Empty<RuleIssue>(), 1)));
        var (vm, workspace, _) = await Create(service, a);
        var firstRun = vm.PendingEvaluation;
        Assert.True(vm.IsEvaluating);

        await Open(workspace, b);
        await vm.PendingEvaluation;
        first.SetResult(new FindingsResult(new[] { Finding(a, "a1", FindingSeverity.Error, "From a", 1) }, Array.Empty<RuleIssue>(), 1));
        await firstRun;

        Assert.Equal(new[] { "From b" }, vm.Groups.SelectMany(g => g.Findings).Select(f => f.Title));
        Assert.Equal(1, vm.FindingCount);
    }

    [Fact]
    public async Task Closing_the_bundle_clears_everything()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _) => Task.FromResult(new FindingsResult(
            new[] { Finding(a, "1", FindingSeverity.Error, "Crash", 1) }, Array.Empty<RuleIssue>(), 1)));
        var (vm, workspace, _) = await Create(service, a);
        await vm.PendingEvaluation;
        Assert.True(vm.HasFindings);

        workspace.Close();

        Assert.Empty(vm.Groups);
        Assert.Empty(vm.Attention);
        Assert.False(vm.HasFindings);
        Assert.Equal("Problems", vm.TabHeader);
        Assert.Contains("Open an archive or folder", vm.StatusText);
    }

    [Fact]
    public async Task A_failing_evaluation_is_reported_and_does_not_throw()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _) => throw new InvalidOperationException("engine down"));
        var (vm, _, _) = await Create(service, a);

        await vm.PendingEvaluation;

        Assert.Contains("engine down", vm.StatusText);
        Assert.False(vm.IsEvaluating);
        Assert.Contains(_output.Entries, e => e.Source == "Rules" && e.Message.Contains("engine down"));
    }
}
