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

/// <summary>A file that could not be fully read, with why and what to try; activating it opens the file.</summary>
public sealed partial class FileProblemViewModel : ObservableObject
{
    public FileProblemViewModel(FileProblem problem)
    {
        Problem = problem;
        Location = problem.Location;
        Title = problem.Artifact.Name;
        Message = problem.Message;
        Remedy = problem.Remedy;
        Where = problem.Artifact.ProvenanceDisplay;
        Severity = problem.Kind switch
        {
            FileProblemKind.Failed => FindingSeverity.Error,
            FileProblemKind.Partial => FindingSeverity.Warning,
            _ => FindingSeverity.Information,
        };
    }

    public FileProblem Problem { get; }
    public DiagnosticLocation Location { get; }
    public string Title { get; }
    public string Message { get; }
    public string Remedy { get; }

    /// <summary>The route through the bundle to the file.</summary>
    public string Where { get; }

    public FindingSeverity Severity { get; }

    // Present so the tree's container style can bind the same properties on every node kind.
    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>Files with the same kind of problem, shown together under one heading.</summary>
public sealed partial class FileProblemGroupViewModel : ObservableObject
{
    public FileProblemGroupViewModel(FileProblemKind kind, IEnumerable<FileProblemViewModel> items)
    {
        Kind = kind;
        foreach (var item in items)
        {
            Items.Add(item);
        }

        Title = string.Create(CultureInfo.CurrentCulture, $"{Heading(kind)} ({Items.Count:N0})");
        Severity = Items.Count > 0 ? Items.Max(i => i.Severity) : FindingSeverity.Information;

        // The ones that need attention open; the long tail (no viewer, empty) stays folded.
        _isExpanded = kind is FileProblemKind.Failed or FileProblemKind.Partial;
    }

    public FileProblemKind Kind { get; }
    public string Title { get; }
    public FindingSeverity Severity { get; }
    public ObservableCollection<FileProblemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    private static string Heading(FileProblemKind kind) => kind switch
    {
        FileProblemKind.Failed => "Files that could not be read",
        FileProblemKind.Partial => "Files only partly read",
        FileProblemKind.NoViewer => "Files with no viewer yet",
        FileProblemKind.Caution => "Opened, with a caution",
        _ => "Empty files",
    };
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

    private const string NoWorkspaceText = "Open an archive or folder to evaluate the built-in rules.";

    private readonly WorkspaceViewModel _workspace;
    private readonly IFindingsService _service;
    private readonly DocumentHostViewModel _documents;
    private readonly IOutputLog _output;
    private readonly IFileHealthService? _health;
    private CancellationTokenSource? _cts;
    private int _runId;

    public ProblemsViewModel(
        WorkspaceViewModel workspace,
        IFindingsService service,
        DocumentHostViewModel documents,
        IOutputLog output,
        IFileHealthService? health = null)
    {
        _workspace = workspace;
        _service = service;
        _documents = documents;
        _output = output;
        _health = health;
        _statusText = NoWorkspaceText;
        _workspace.WorkspaceChanged += (_, _) => Restart();
    }

    public ObservableCollection<ProblemGroupViewModel> Groups { get; } = new();

    /// <summary>Files that could not be fully read, by kind of problem.</summary>
    public ObservableCollection<FileProblemGroupViewModel> FileGroups { get; } = new();

    /// <summary>
    /// Everything the panel lists: the finding groups, then the file problem groups, leaving out the severities
    /// switched off with the Errors, Warnings and Messages buttons.
    /// </summary>
    public ObservableCollection<object> Items { get; } = new();

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

    /// <summary>Files that failed or were only partly read; the ones worth the engineer's attention.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeader))]
    private int _unreadableFileCount;

    // The three filter buttons. Each counts the findings and the files of its severity, whether or not it is shown.
    [ObservableProperty]
    private bool _showErrors = true;

    [ObservableProperty]
    private bool _showWarnings = true;

    [ObservableProperty]
    private bool _showMessages = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorsLabel))]
    private int _errorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningsLabel))]
    private int _warningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessagesLabel))]
    private int _messageCount;

    public string ErrorsLabel => Label(ErrorCount, "Error");

    public string WarningsLabel => Label(WarningCount, "Warning");

    public string MessagesLabel => Label(MessageCount, "Message");

    private static string Label(int count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    partial void OnShowErrorsChanged(bool value) => RebuildItems();

    partial void OnShowWarningsChanged(bool value) => RebuildItems();

    partial void OnShowMessagesChanged(bool value) => RebuildItems();

    private bool IsShown(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => ShowErrors,
        FindingSeverity.Warning => ShowWarnings,
        _ => ShowMessages,
    };

    private void RebuildItems()
    {
        Items.Clear();
        foreach (var group in Groups.Where(g => IsShown(g.Severity)))
        {
            Items.Add(group);
        }

        foreach (var group in FileGroups.Where(g => IsShown(g.Severity)))
        {
            Items.Add(group);
        }

        ErrorCount = CountOf(FindingSeverity.Error);
        WarningCount = CountOf(FindingSeverity.Warning);
        MessageCount = CountOf(FindingSeverity.Information);
    }

    private int CountOf(FindingSeverity severity) =>
        Groups.Where(g => g.Severity == severity).Sum(g => g.Findings.Count)
        + FileGroups.Where(g => g.Severity == severity).Sum(g => g.Items.Count);

    public bool HasFindings => FindingCount > 0;

    public bool HasAttention => Attention.Count > 0;

    public string TabHeader => (FindingCount, UnreadableFileCount) switch
    {
        (0, 0) => "Problems",
        (var findings, 0) => string.Create(CultureInfo.CurrentCulture, $"Problems ({findings:N0})"),
        (0, var files) => string.Create(CultureInfo.CurrentCulture, $"Problems ({files:N0} {(files == 1 ? "file" : "files")})"),
        var (findings, files) => string.Create(CultureInfo.CurrentCulture, $"Problems ({findings:N0} \u00B7 {files:N0} {(files == 1 ? "file" : "files")})"),
    };

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

            if (_health is not null)
            {
                await CheckFilesAsync(run, workspace, result, token).ConfigureAwait(true);
                return;
            }

            IsEvaluating = false;
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

        RebuildItems();
        StatusText = Summarise(models, result);
    }

    private async Task CheckFilesAsync(int run, InvestigationWorkspace workspace, FindingsResult result, CancellationToken token)
    {
        var findingsStatus = StatusText;
        var progress = new Progress<FileHealthProgress>(p =>
        {
            if (run == _runId && IsEvaluating)
            {
                StatusText = string.Create(CultureInfo.CurrentCulture, $"Checking files... {p.FilesChecked:N0} of {p.FilesTotal:N0}");
            }
        });

        try
        {
            var problems = await Task.Run(() => _health!.CheckAsync(workspace.Artifacts, progress, token), token).ConfigureAwait(true);
            if (run != _runId)
            {
                return;
            }

            ShowFileProblems(problems);
            StatusText = findingsStatus + FileSummary(problems);
        }
        catch (OperationCanceledException)
        {
            // A newer bundle replaced this one.
        }
        catch (Exception ex)
        {
            if (run == _runId)
            {
                _output.Write(OutputSeverity.Error, "Rules", "Checking files failed: " + ex.Message);
                StatusText = findingsStatus;
            }
        }
        finally
        {
            if (run == _runId)
            {
                IsEvaluating = false;
            }
        }
    }

    private void ShowFileProblems(IReadOnlyList<FileProblem> problems)
    {
        foreach (var group in problems
                     .GroupBy(p => p.Kind)
                     .OrderByDescending(g => g.Key)
                     .Select(g => new FileProblemGroupViewModel(g.Key, g.Select(p => new FileProblemViewModel(p)))))
        {
            FileGroups.Add(group);
        }

        RebuildItems();

        UnreadableFileCount = problems.Count(p => p.Kind is FileProblemKind.Failed or FileProblemKind.Partial);
    }

    private static string FileSummary(IReadOnlyList<FileProblem> problems)
    {
        if (problems.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        void Add(FileProblemKind kind, string text)
        {
            var n = problems.Count(p => p.Kind == kind);
            if (n > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{n:N0} {text}"));
            }
        }

        Add(FileProblemKind.Failed, "could not be read");
        Add(FileProblemKind.Partial, "only partly read");
        Add(FileProblemKind.Caution, "opened with a caution");
        Add(FileProblemKind.NoViewer, "have no viewer yet");
        Add(FileProblemKind.Empty, "empty");
        return " Files: " + string.Join(", ", parts) + ".";
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
        FileGroups.Clear();
        Items.Clear();
        Attention.Clear();
        FindingCount = 0;
        UnreadableFileCount = 0;
        ErrorCount = 0;
        WarningCount = 0;
        MessageCount = 0;
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

    /// <summary>Opens a file that could not be fully read, at the first place that went wrong when that is known.</summary>
    [RelayCommand]
    private void OpenFileProblem(FileProblemViewModel? problem)
    {
        if (problem is not null)
        {
            _documents.OpenLocation(problem.Location);
        }
    }

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
