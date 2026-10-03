using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Rules.Custom;

namespace DiagnosticStudio.App.ViewModels.CustomRules;

/// <summary>A finding produced by pressing Test, with a way to open the first thing it is based on.</summary>
public sealed class TestFindingViewModel
{
    public TestFindingViewModel(Finding finding, Action<DiagnosticLocation> open)
    {
        Severity = finding.Severity;
        Title = finding.Title;
        Summary = finding.Description;
        EvidenceCount = finding.Evidence.Count;
        Evidence = finding.Evidence.Count == 0 ? string.Empty : finding.Evidence[0].Description ?? finding.Evidence[0].Location.ToString();
        var first = finding.Evidence.Count > 0 ? finding.Evidence[0].Location : null;
        OpenCommand = new RelayCommand(() =>
        {
            if (first is not null)
            {
                open(first);
            }
        });
    }

    public FindingSeverity Severity { get; }
    public string Title { get; }
    public string Summary { get; }
    public int EvidenceCount { get; }
    public string Evidence { get; }

    public string EvidenceLabel => string.Create(CultureInfo.CurrentCulture, $"{EvidenceCount:N0} evidence {(EvidenceCount == 1 ? "item" : "items")}");

    public IRelayCommand OpenCommand { get; }
}

/// <summary>
/// The Custom Rules tab: the user's own rules (what to look at and what to report), where they are written,
/// imported, exported and tried out. Rules live in the user's own profile; nothing in what is being investigated can add
/// or change one.
/// </summary>
public sealed partial class RulesDocumentViewModel : DocumentViewModel
{
    private const long MaxImportBytes = 20 * 1024 * 1024;

    private readonly ICustomRuleStore _store;
    private readonly ICustomRuleEngine _engine;
    private readonly IFileDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly WorkspaceViewModel _workspace;
    private readonly IOutputLog _output;
    private CancellationTokenSource? _testCts;

    public RulesDocumentViewModel(
        ICustomRuleStore store,
        ICustomRuleEngine engine,
        IFileDialogService dialogs,
        IClipboardService clipboard,
        WorkspaceViewModel workspace,
        IOutputLog output)
    {
        _store = store;
        _engine = engine;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _workspace = workspace;
        _output = output;

        var rules = store.Load(out var problem);
        foreach (var rule in rules)
        {
            Add(new RuleItemViewModel(rule), select: false);
        }

        Selected = Rules.FirstOrDefault();
        if (problem is not null)
        {
            _statusText = problem;
            _output.Write(OutputSeverity.Warning, "Rules", problem);
        }
    }

    public override string Title => "Custom Rules";

    public ObservableCollection<RuleItemViewModel> Rules { get; } = new();

    /// <summary>Raised after the rules were saved, so that they run again over what is open.</summary>
    public event EventHandler? Saved;

    /// <summary>Opens a place in the open investigation; set by the document host.</summary>
    public Func<DiagnosticLocation, bool>? OpenLocation { get; set; }

    public string StorePath => (_store as FileCustomRuleStore)?.FilePath ?? string.Empty;

    // ---- the help shown beside the editor ----

    public string HowItWorks => CustomRuleHelp.HowItWorks;

    public string Tips => CustomRuleHelp.Tips;

    public IReadOnlyList<RuleExample> Examples => CustomRuleHelp.Examples;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExample))]
    [NotifyCanExecuteChangedFor(nameof(AddExampleCommand))]
    private RuleExample? _selectedExample = CustomRuleHelp.Examples[0];

    public bool HasExample => SelectedExample is not null;

    [RelayCommand(CanExecute = nameof(HasExample))]
    private void AddExample()
    {
        if (SelectedExample is { } example)
        {
            AddNew(example.Rule);
        }
    }

    // ---- the list ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(TestCommand))]
    private RuleItemViewModel? _selected;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Rules.Count == 0;

    private void Add(RuleItemViewModel item, bool select)
    {
        item.Edited += (_, _) => IsDirty = true;
        Rules.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        if (select)
        {
            Selected = item;
        }
    }

    private ISet<string> UsedIds() => new HashSet<string>(Rules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void NewRule() => AddNew(new CustomRule { Name = "New rule", Selector = new CustomRuleSelector { Kind = CustomRuleKind.Event } });

    private void AddNew(CustomRule template)
    {
        var id = CustomRuleJson.NewId(template.Name, UsedIds());
        Add(new RuleItemViewModel(template with { Id = id, Enabled = false }), select: true);
        IsDirty = true;
        StatusText = "Added. It is switched off until you tick Enabled; use Test to try it on what is open, then Save.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } rule)
        {
            return;
        }

        var at = Rules.IndexOf(rule);
        Rules.Remove(rule);
        Selected = Rules.Count == 0 ? null : Rules[Math.Min(at, Rules.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
        IsDirty = true;
        StatusText = $"Removed '{rule.DisplayName}'. Save to make it permanent.";
    }

    // ---- storage, import and export ----

    [RelayCommand]
    private void Save()
    {
        if (!TryCollect(out var rules))
        {
            return;
        }

        if (_store.Save(rules) is { } problem)
        {
            StatusText = problem;
            _output.Write(OutputSeverity.Warning, "Rules", problem);
            return;
        }

        IsDirty = false;
        StatusText = string.Create(CultureInfo.CurrentCulture, $"Saved {rules.Count:N0} {(rules.Count == 1 ? "rule" : "rules")}. Enabled rules run again over what is open.");
        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Import()
    {
        if (_dialogs.PickJsonToOpen("Import rules") is not { } path)
        {
            return;
        }

        try
        {
            if (new FileInfo(path).Length > MaxImportBytes)
            {
                StatusText = "That file is too large to be a set of rules.";
                return;
            }

            if (!CustomRuleJson.TryParse(File.ReadAllText(path), out var imported, out var error))
            {
                StatusText = "Nothing was imported: " + error;
                return;
            }

            // A rule that runs by itself on every open must be read and switched on by the person who will own it.
            var rules = CustomRuleJson.GiveUniqueIds(imported, UsedIds()).Select(r => r with { Enabled = false }).ToList();
            foreach (var rule in rules)
            {
                Add(new RuleItemViewModel(rule), select: false);
            }

            Selected = Rules.Count > 0 && rules.Count > 0 ? Rules.First(r => r.Id == rules[0].Id) : Selected;
            IsDirty = IsDirty || rules.Count > 0;
            StatusText = string.Create(
                CultureInfo.CurrentCulture,
                $"Imported {rules.Count:N0} {(rules.Count == 1 ? "rule" : "rules")}. They are switched off: read each one, then tick Enabled and Save.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "The file could not be read: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Export()
    {
        if (!TryCollect(out var rules))
        {
            return;
        }

        if (_dialogs.PickJsonToSave("Export rules", "diagnostic-studio-rules.json") is not { } path)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, CustomRuleJson.Serialize(rules));
            StatusText = string.Create(CultureInfo.CurrentCulture, $"Exported {rules.Count:N0} {(rules.Count == 1 ? "rule" : "rules")} to {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "The file could not be written: " + ex.Message;
        }
    }

    private bool TryCollect(out List<CustomRule> rules)
    {
        rules = new List<CustomRule>();
        foreach (var item in Rules)
        {
            rules.Add(item.ToRule(out var problems));
            if (problems.Count > 0)
            {
                Selected = item;
                StatusText = $"'{item.DisplayName}': {problems[0]}";
                return false;
            }
        }

        return true;
    }

    // ---- trying a rule on what is open ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    [ObservableProperty]
    private string _testSummary = string.Empty;

    public ObservableCollection<TestFindingViewModel> TestFindings { get; } = new();

    public ObservableCollection<string> TestProblems { get; } = new();

    public bool HasTestResult => TestSummary.Length > 0;

    partial void OnTestSummaryChanged(string value) => OnPropertyChanged(nameof(HasTestResult));

    partial void OnSelectedChanged(RuleItemViewModel? value) => ClearTest();

    private void ClearTest()
    {
        _testCts?.Cancel();
        TestSummary = string.Empty;
        TestFindings.Clear();
        TestProblems.Clear();
    }

    private bool CanTest() => Selected is not null && !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }

        ClearTest();
        if (_workspace.Current is not { } workspace)
        {
            TestSummary = "Nothing is open. Open an archive or folder, then press Test: the rule runs on what is open.";
            return;
        }

        var rule = item.ToRule(out var parseProblems);
        if (parseProblems.Count > 0)
        {
            ShowProblems("The rule was not run.", parseProblems);
            return;
        }

        _testCts = new CancellationTokenSource();
        var token = _testCts.Token;
        IsTesting = true;
        TestSummary = "Running...";
        try
        {
            var artifacts = workspace.Artifacts;
            var run = await Task.Run(() => _engine.RunAsync(rule, artifacts, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            foreach (var finding in run.Findings)
            {
                TestFindings.Add(new TestFindingViewModel(finding, location => OpenLocation?.Invoke(location)));
            }

            foreach (var problem in run.Problems)
            {
                TestProblems.Add(problem);
            }

            TestSummary = Describe(run, rule);
        }
        catch (OperationCanceledException)
        {
            // Another rule was selected or the tab was closed.
        }
        catch (Exception ex)
        {
            ShowProblems("The rule could not be run.", new[] { ex.Message });
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void ShowProblems(string summary, IEnumerable<string> problems)
    {
        TestSummary = summary;
        foreach (var problem in problems)
        {
            TestProblems.Add(problem);
        }
    }

    private static string Describe(CustomRuleRun run, CustomRule rule)
    {
        var culture = CultureInfo.CurrentCulture;
        if (!run.Ran)
        {
            return run.Problems.Count > 0
                ? "The rule was not run."
                : rule.Trigger == CustomRuleTrigger.Missing
                    ? "No file in what is open is named like the 'In files named' setting, so there is nothing to check."
                    : "Nothing in what is open matches the Look at settings, so there is nothing to report.";
        }

        var found = run.Findings.Count;
        var records = string.Create(culture, $"{run.RecordsMatched:N0} matching {(run.RecordsMatched == 1 ? "record" : "records")}");
        var counted = string.Create(culture, $"{records} · {found:N0} {(found == 1 ? "finding" : "findings")}");
        var hint = found == 0 && run.RecordsMatched > 0
            ? rule.Trigger switch
            {
                CustomRuleTrigger.Burst => string.Create(culture, $" · none reported: never {rule.MinMatches:N0} within {rule.WindowMinutes:N0} minutes"),
                CustomRuleTrigger.Gap => string.Create(culture, $" · none reported: no silence longer than {rule.WindowMinutes:N0} minutes (matches need a time)"),
                _ when rule.MinMatches > 1 => string.Create(culture, $" · none reported: no group reached the minimum of {rule.MinMatches:N0} matches"),
                _ => " · none reported",
            }
            : string.Empty;
        return run.Problems.Count == 0
            ? counted + hint
            : counted + hint + string.Create(culture, $" · {run.Problems.Count:N0} {(run.Problems.Count == 1 ? "problem" : "problems")}");
    }

    public override void OnClosed()
    {
        _testCts?.Cancel();
    }
}
