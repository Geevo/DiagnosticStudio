using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.Rules.Custom;

namespace DiagnosticStudio.App.ViewModels.CustomRules;

/// <summary>A value with the words shown for it in a list.</summary>
public sealed record Choice<T>(T Value, string Label);

/// <summary>One custom rule as it is being edited: every setting as text the user can change.</summary>
public sealed partial class RuleItemViewModel : ObservableObject
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly char[] ListSeparators = { ',', ';', ' ', '\t', '\r', '\n' };

    public RuleItemViewModel(CustomRule rule)
    {
        var selector = rule.Selector;
        Id = rule.Id;
        _name = rule.Name;
        _description = rule.Description;
        _enabled = rule.Enabled;
        _kind = selector.Kind;
        _file = selector.File ?? string.Empty;
        _excludeFile = selector.ExcludeFile ?? string.Empty;
        _provider = selector.Provider ?? string.Empty;
        _excludeProvider = selector.ExcludeProvider ?? string.Empty;
        _eventIds = string.Join(", ", selector.EventIds);
        _excludeEventIds = string.Join(", ", selector.ExcludeEventIds);
        _levels = string.Join(", ", selector.Levels);
        _contains = selector.Contains ?? string.Empty;
        _notContains = selector.NotContains ?? string.Empty;
        _regex = selector.Regex ?? string.Empty;
        _notRegex = selector.NotRegex ?? string.Empty;
        _matchCase = selector.MatchCase;
        _dataField = selector.DataField ?? string.Empty;
        _after = selector.After?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
        _before = selector.Before?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
        _severity = rule.Severity;
        _title = rule.Title ?? string.Empty;
        _groupBy = rule.GroupBy;
        _trigger = rule.Trigger;
        _minMatches = rule.MinMatches.ToString(CultureInfo.InvariantCulture);
        _windowMinutes = rule.WindowMinutes.ToString(CultureInfo.InvariantCulture);

        // The advanced view starts on when the rule already uses one of its settings, so nothing it does is hidden.
        _showAdvanced = UsesAdvancedSettings;
        Revalidate();
    }

    /// <summary>Stable identity of the rule; it does not change when the rule is renamed.</summary>
    public string Id { get; }

    public static IReadOnlyList<Choice<CustomRuleKind>> Kinds { get; } = new[]
    {
        new Choice<CustomRuleKind>(CustomRuleKind.Event, "Event log entries"),
        new Choice<CustomRuleKind>(CustomRuleKind.Line, "Lines of text files"),
    };

    public static IReadOnlyList<Choice<CustomRuleSeverity>> Severities { get; } = new[]
    {
        new Choice<CustomRuleSeverity>(CustomRuleSeverity.Error, "Error"),
        new Choice<CustomRuleSeverity>(CustomRuleSeverity.Warning, "Warning"),
        new Choice<CustomRuleSeverity>(CustomRuleSeverity.Information, "Information"),
    };

    public static IReadOnlyList<Choice<CustomRuleGrouping>> Groupings { get; } = new[]
    {
        new Choice<CustomRuleGrouping>(CustomRuleGrouping.All, "One finding for all matches"),
        new Choice<CustomRuleGrouping>(CustomRuleGrouping.Each, "One finding for each match"),
        new Choice<CustomRuleGrouping>(CustomRuleGrouping.File, "One finding for each file"),
        new Choice<CustomRuleGrouping>(CustomRuleGrouping.Provider, "One finding for each provider (events)"),
        new Choice<CustomRuleGrouping>(CustomRuleGrouping.EventId, "One finding for each event id (events)"),
        new Choice<CustomRuleGrouping>(CustomRuleGrouping.Capture, "One finding for each captured value (regex)"),
    };

    public static IReadOnlyList<Choice<CustomRuleTrigger>> Triggers { get; } = new[]
    {
        new Choice<CustomRuleTrigger>(CustomRuleTrigger.Count, "There are at least this many matches"),
        new Choice<CustomRuleTrigger>(CustomRuleTrigger.Burst, "Matches come in a burst (this many within a window)"),
        new Choice<CustomRuleTrigger>(CustomRuleTrigger.Gap, "There is a silence between two matches"),
        new Choice<CustomRuleTrigger>(CustomRuleTrigger.Missing, "A file has too few matches (none, by default)"),
    };

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private CustomRuleSeverity _severity;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private CustomRuleGrouping _groupBy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesWindow), nameof(UsesGrouping), nameof(MinMatchesLabel), nameof(WindowLabel))]
    private CustomRuleTrigger _trigger;

    [ObservableProperty]
    private string _minMatches;

    [ObservableProperty]
    private string _windowMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEventRule), nameof(IsLineRule))]
    private CustomRuleKind _kind;

    [ObservableProperty]
    private string _file;

    [ObservableProperty]
    private string _excludeFile;

    [ObservableProperty]
    private string _provider;

    [ObservableProperty]
    private string _excludeProvider;

    [ObservableProperty]
    private string _eventIds;

    [ObservableProperty]
    private string _excludeEventIds;

    [ObservableProperty]
    private string _levels;

    [ObservableProperty]
    private string _contains;

    [ObservableProperty]
    private string _notContains;

    [ObservableProperty]
    private string _regex;

    [ObservableProperty]
    private string _notRegex;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string _dataField;

    [ObservableProperty]
    private string _after;

    [ObservableProperty]
    private string _before;

    /// <summary>Whether the editor shows the less common settings as well as the everyday ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenAdvanced))]
    private bool _showAdvanced;

    /// <summary>Why the rule cannot run as it stands; empty when it can.</summary>
    [ObservableProperty]
    private string _validation = string.Empty;

    /// <summary>True when the rule sets something that only the advanced view shows.</summary>
    public bool UsesAdvancedSettings =>
        Levels.Length + NotContains.Length + Regex.Length + NotRegex.Length + ExcludeFile.Length + ExcludeProvider.Length
        + ExcludeEventIds.Length + DataField.Length + After.Length + Before.Length + Title.Length > 0
        || MatchCase
        || GroupBy != CustomRuleGrouping.All
        || Trigger != CustomRuleTrigger.Count
        || MinMatches.Trim() != "1";

    /// <summary>The advanced view is off but the rule still uses a setting from it; the editor says so.</summary>
    public bool HasHiddenAdvanced => !ShowAdvanced && UsesAdvancedSettings;

    public bool IsEventRule => Kind == CustomRuleKind.Event;

    public bool IsLineRule => Kind == CustomRuleKind.Line;

    /// <summary>Burst and silence are measured against a window of minutes.</summary>
    public bool UsesWindow => Trigger is CustomRuleTrigger.Burst or CustomRuleTrigger.Gap;

    /// <summary>A file with too few matches is reported for the file itself, so grouping does not apply.</summary>
    public bool UsesGrouping => Trigger != CustomRuleTrigger.Missing;

    public string MinMatchesLabel => Trigger switch
    {
        CustomRuleTrigger.Burst => "Number of matches that make a burst",
        CustomRuleTrigger.Missing => "Report a file with fewer matches than this",
        CustomRuleTrigger.Gap => "Matches needed (leave at 1)",
        _ => "Only report when there are at least this many matches",
    };

    public string WindowLabel => Trigger == CustomRuleTrigger.Gap
        ? "Silence is a gap longer than this many minutes"
        : "Within this many minutes";

    /// <summary>Shown in the list: the name, or a placeholder while it is empty.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed rule)" : Name;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name))
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        if (e.PropertyName is not (nameof(Validation) or nameof(DisplayName) or nameof(ShowAdvanced) or nameof(HasHiddenAdvanced)
            or nameof(UsesWindow) or nameof(UsesGrouping) or nameof(MinMatchesLabel) or nameof(WindowLabel)
            or nameof(IsEventRule) or nameof(IsLineRule)))
        {
            OnPropertyChanged(nameof(HasHiddenAdvanced));
            Revalidate();
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when anything the user can change was changed.</summary>
    public event EventHandler? Edited;

    /// <summary>The rule as it is now. <paramref name="problems"/> lists text that could not be read as part of it.</summary>
    public CustomRule ToRule(out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        var isEvent = Kind == CustomRuleKind.Event;
        var ids = ReadIds(EventIds, "Event ids", found);
        var skipIds = ReadIds(ExcludeEventIds, "Leave out event ids", found);

        var minimum = 1;
        if (!int.TryParse(MinMatches.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out minimum) || minimum < 1)
        {
            found.Add("The number of matches must be a whole number, 1 or more.");
            minimum = 1;
        }

        var window = 60;
        if (UsesWindow && (!int.TryParse(WindowMinutes.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out window) || window < 1))
        {
            found.Add("The window must be a whole number of minutes, 1 or more.");
            window = 60;
        }

        var after = ReadTime(After, "From", found);
        var before = ReadTime(Before, "Until", found);

        problems = found;
        return new CustomRule
        {
            Id = Id,
            Name = Name.Trim(),
            Description = Description.Trim(),
            Enabled = Enabled,
            Severity = Severity,
            Title = Blank(Title),
            GroupBy = UsesGrouping ? GroupBy : CustomRuleGrouping.All,
            Trigger = Trigger,
            MinMatches = minimum,
            WindowMinutes = UsesWindow ? window : 60,
            Selector = new CustomRuleSelector
            {
                Kind = Kind,
                File = Blank(File),
                ExcludeFile = Blank(ExcludeFile),
                Provider = isEvent ? Blank(Provider) : null,
                ExcludeProvider = isEvent ? Blank(ExcludeProvider) : null,
                EventIds = isEvent ? ids : Array.Empty<uint>(),
                ExcludeEventIds = isEvent ? skipIds : Array.Empty<uint>(),
                Levels = isEvent ? Split(Levels).Select(Canonical).ToList() : Array.Empty<string>(),
                Contains = Blank(Contains),
                NotContains = Blank(NotContains),
                Regex = Blank(Regex),
                NotRegex = Blank(NotRegex),
                MatchCase = MatchCase,
                DataField = isEvent ? Blank(DataField) : null,
                After = after,
                Before = before,
            },
        };
    }

    private void Revalidate()
    {
        var rule = ToRule(out var parse);
        Validation = string.Join(Environment.NewLine, parse.Concat(CustomRuleValidator.Validate(rule)));
    }

    private static List<uint> ReadIds(string text, string what, List<string> problems)
    {
        var ids = new List<uint>();
        foreach (var token in Split(text))
        {
            var number = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : (uint?)null
                : uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : null;
            if (number is { } value)
            {
                ids.Add(value);
            }
            else
            {
                problems.Add($"{what}: '{token}' is not an event id. Use numbers separated by commas.");
            }
        }

        return ids;
    }

    /// <summary>Times are read as UTC: that is what an event log records, and what lines are compared with.</summary>
    private static DateTime? ReadTime(string text, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            return time;
        }

        problems.Add($"{what}: '{text.Trim()}' is not a date and time. Write it like 2026-03-14 09:30.");
        return null;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static IEnumerable<string> Split(string text) => text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries);

    // "error" is accepted as "Error"; anything that is not a level is kept as typed so the validator can name it.
    private static string Canonical(string level) =>
        CustomRuleValidator.KnownLevels.FirstOrDefault(k => string.Equals(k, level, StringComparison.OrdinalIgnoreCase)) ?? level;
}
