using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>One piece of source evidence behind a finding; activating it opens the source.</summary>
public sealed partial class EvidenceViewModel : ObservableObject
{
    public EvidenceViewModel(FindingEvidence evidence, string artifactName)
    {
        Location = evidence.Location;
        ArtifactName = artifactName;
        Text = evidence.Description ?? evidence.Location.ToString();
    }

    public DiagnosticLocation Location { get; }
    public string ArtifactName { get; }
    public string Text { get; }

    // Present so the tree's container style can bind the same properties on every node kind.
    [ObservableProperty]
    private bool _isExpanded;
}

public sealed partial class FindingViewModel : ObservableObject
{
    public FindingViewModel(Finding finding, Func<Guid, string> artifactName)
    {
        Finding = finding;
        Severity = finding.Severity;
        Title = finding.Title;
        Description = finding.Description;
        Notes = finding.Notes;

        foreach (var evidence in finding.Evidence)
        {
            Evidence.Add(new EvidenceViewModel(evidence, artifactName(evidence.Location.ArtifactId)));
        }

        ArtifactName = Evidence.Count > 0 ? Evidence[0].ArtifactName : string.Empty;
    }

    public Finding Finding { get; }
    public FindingSeverity Severity { get; }
    public string Title { get; }
    public string Description { get; }
    public string? Notes { get; }

    /// <summary>The artifact of the first piece of evidence, for display next to the title.</summary>
    public string ArtifactName { get; }

    public ObservableCollection<EvidenceViewModel> Evidence { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;
}

public sealed partial class ProblemGroupViewModel : ObservableObject
{
    public ProblemGroupViewModel(FindingSeverity severity, IEnumerable<FindingViewModel> findings)
    {
        Severity = severity;
        foreach (var finding in findings)
        {
            Findings.Add(finding);
        }

        Title = string.Create(CultureInfo.CurrentCulture, $"{GroupName(severity)} ({Findings.Count:N0})");
    }

    public FindingSeverity Severity { get; }
    public string Title { get; }
    public ObservableCollection<FindingViewModel> Findings { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    private static string GroupName(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => "Errors",
        FindingSeverity.Warning => "Warnings",
        _ => "Information",
    };
}

/// <summary>
/// Findings from the deterministic rules for the open bundle, grouped by severity. Evaluation runs in the background
/// whenever a bundle is opened; every piece of evidence navigates to its source through <see cref="DiagnosticLocation"/>.
/// </summary>
public sealed partial class ProblemsViewModel : ObservableObject
{
    /// <summary>Findings the Overview's Attention Needed section shows; the full list stays here.</summary>
    public const int AttentionLimit = 5;

    private const string NoWorkspaceText = "Open a bundle to evaluate the built-in rules.";

    private readonly WorkspaceViewModel _workspace;
    private readonly IFindingsService _service;
    private readonly DocumentHostViewModel _documents;
    private readonly IOutputLog _output;
    private CancellationTokenSource? _cts;
    private int _runId;

    public ProblemsViewModel(
        WorkspaceViewModel workspace,
        IFindingsService service,
        DocumentHostViewModel documents,
        IOutputLog output)
    {
        _workspace = workspace;
        _service = service;
        _documents = documents;
        _output = output;
        _statusText = NoWorkspaceText;
        _workspace.WorkspaceChanged += (_, _) => Restart();
    }

    public ObservableCollection<ProblemGroupViewModel> Groups { get; } = new();

    /// <summary>The most severe findings, for the Overview.</summary>
    public ObservableCollection<FindingViewModel> Attention { get; } = new();

    /// <summary>The evaluation in progress or most recently finished; lets callers and tests await it.</summary>
    public Task PendingEvaluation { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _isEvaluating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeader), nameof(HasFindings))]
    private int _findingCount;

    public bool HasFindings => FindingCount > 0;

    public bool HasAttention => Attention.Count > 0;

    public string TabHeader => FindingCount > 0
        ? string.Create(CultureInfo.CurrentCulture, $"Problems ({FindingCount:N0})")
        : "Problems";

    private void Restart()
    {
        _runId++;
        _cts?.Cancel();
        Clear();

        if (_workspace.Current is not { } workspace)
        {
            StatusText = NoWorkspaceText;
            IsEvaluating = false;
            PendingEvaluation = Task.CompletedTask;
            return;
        }

        _cts = new CancellationTokenSource();
        PendingEvaluation = EvaluateAsync(_runId, workspace, _cts.Token);
    }

    private async Task EvaluateAsync(int run, InvestigationWorkspace workspace, CancellationToken token)
    {
        IsEvaluating = true;
        StatusText = "Evaluating rules...";
        var progress = new Progress<FindingsProgress>(p =>
        {
            if (run == _runId && IsEvaluating)
            {
                StatusText = string.Create(CultureInfo.CurrentCulture, $"Evaluating rules... {p.ArtifactsEvaluated:N0} of {p.ArtifactsTotal:N0} artifacts");
            }
        });

        try
        {
            var result = await Task.Run(() => _service.EvaluateAsync(workspace.Artifacts, progress, token), token).ConfigureAwait(true);
            if (run != _runId)
            {
                return; // superseded by another bundle
            }

            Show(workspace, result);
        }
        catch (OperationCanceledException)
        {
            // A newer bundle replaced this one; nothing to report.
        }
        catch (Exception ex)
        {
            if (run == _runId)
            {
                _output.Write(OutputSeverity.Error, "Rules", "Rule evaluation failed: " + ex.Message);
                StatusText = "Rule evaluation failed: " + ex.Message;
                IsEvaluating = false;
            }
        }
    }

    private void Show(InvestigationWorkspace workspace, FindingsResult result)
    {
        string NameOf(Guid id) => workspace.Find(id)?.Name ?? id.ToString("N");

        var models = result.Findings.Select(f => new FindingViewModel(f, NameOf)).ToList();
        foreach (var group in models.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            Groups.Add(new ProblemGroupViewModel(group.Key, group));
        }

        foreach (var finding in models.Where(f => f.Severity >= FindingSeverity.Warning).Take(AttentionLimit))
        {
            Attention.Add(finding);
        }

        FindingCount = models.Count;
        OnPropertyChanged(nameof(HasAttention));

        foreach (var issue in result.Issues)
        {
            _output.Write(OutputSeverity.Warning, "Rules", $"{issue.Subject}: {issue.Message}");
        }

        StatusText = Summarise(models, result);
        IsEvaluating = false;
    }

    private static string Summarise(IReadOnlyList<FindingViewModel> findings, FindingsResult result)
    {
        var culture = CultureInfo.CurrentCulture;
        if (findings.Count == 0)
        {
            return string.Create(
                culture,
                $"No findings. The built-in rules examined {result.ArtifactsEvaluated:N0} artifacts; the absence of a finding does not show that nothing is wrong.");
        }

        var errors = findings.Count(f => f.Severity == FindingSeverity.Error);
        var warnings = findings.Count(f => f.Severity == FindingSeverity.Warning);
        var info = findings.Count - errors - warnings;
        return string.Create(
            culture,
            $"{findings.Count:N0} findings: {errors:N0} errors, {warnings:N0} warnings, {info:N0} information. Rules examined {result.ArtifactsEvaluated:N0} artifacts.");
    }

    private void Clear()
    {
        Groups.Clear();
        Attention.Clear();
        FindingCount = 0;
        OnPropertyChanged(nameof(HasAttention));
    }

    /// <summary>Opens the source behind one piece of evidence.</summary>
    [RelayCommand]
    private void OpenEvidence(EvidenceViewModel? evidence)
    {
        if (evidence is not null)
        {
            _documents.OpenLocation(evidence.Location);
        }
    }

    /// <summary>Shows when an evidence item (or a finding's first evidence) happened, among everything else in the bundle.</summary>
    [RelayCommand(CanExecute = nameof(CanShowInTimeline))]
    private Task ShowInTimeline(object? item) => item switch
    {
        EvidenceViewModel evidence => _documents.ShowInTimelineAsync(evidence.Location),
        FindingViewModel { Evidence.Count: > 0 } finding => _documents.ShowInTimelineAsync(finding.Evidence[0].Location),
        _ => Task.CompletedTask,
    };

    private static bool CanShowInTimeline(object? item) =>
        item is EvidenceViewModel or FindingViewModel { Evidence.Count: > 0 };

    /// <summary>Opens the first piece of evidence of a finding, the natural starting point.</summary>
    [RelayCommand]
    private void OpenFinding(FindingViewModel? finding)
    {
        if (finding?.Evidence.FirstOrDefault() is { } first)
        {
            _documents.OpenLocation(first.Location);
        }
    }
}
