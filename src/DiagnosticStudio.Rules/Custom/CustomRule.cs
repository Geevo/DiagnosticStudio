using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>What a custom rule looks at: event log entries or the lines of text files.</summary>
public enum CustomRuleKind
{
    Event,
    Line,
}

/// <summary>How the severity of a finding written by a rule is named in the rule file.</summary>
public enum CustomRuleSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>How the records a rule matched are turned into findings.</summary>
public enum CustomRuleGrouping
{
    /// <summary>One finding for everything that matched.</summary>
    All,

    /// <summary>One finding for each matching record.</summary>
    Each,

    /// <summary>One finding for each file.</summary>
    File,

    /// <summary>Events only: one finding for each provider.</summary>
    Provider,

    /// <summary>Events only: one finding for each event id.</summary>
    EventId,

    /// <summary>One finding for each distinct value the regular expression captures (its group named <c>key</c>, or its first group).</summary>
    Capture,
}

/// <summary>When a group of matches is worth a finding.</summary>
public enum CustomRuleTrigger
{
    /// <summary>The group has at least the minimum number of matches.</summary>
    Count,

    /// <summary>At least the minimum number of matches happen within the window.</summary>
    Burst,

    /// <summary>Two matches in a row are further apart than the window: a silence.</summary>
    Gap,

    /// <summary>A file has fewer matches than expected (none, by default).</summary>
    Missing,
}

/// <summary>Narrows what a rule looks at. Every part that is filled in must be met by a record for it to match.</summary>
public sealed record CustomRuleSelector
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomRuleKind Kind { get; init; } = CustomRuleKind.Event;

    /// <summary>File name patterns (<c>*</c> and <c>?</c>), separated by commas, matched against the file's name. Empty means any file.</summary>
    public string? File { get; init; }

    /// <summary>Files with a name like one of these patterns are skipped.</summary>
    public string? ExcludeFile { get; init; }

    /// <summary>Events only: provider names or patterns, separated by commas, as shown in the event log viewer. Empty means any provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Events only: providers like one of these are skipped.</summary>
    public string? ExcludeProvider { get; init; }

    /// <summary>Events only: event ids to include. Empty means all.</summary>
    public IReadOnlyList<uint> EventIds { get; init; } = Array.Empty<uint>();

    /// <summary>Events only: event ids to skip.</summary>
    public IReadOnlyList<uint> ExcludeEventIds { get; init; } = Array.Empty<uint>();

    /// <summary>Events only: level names to include (Critical, Error, Warning, Information, Verbose). Empty means all.</summary>
    public IReadOnlyList<string> Levels { get; init; } = Array.Empty<string>();

    /// <summary>Text a line, or an event's message (or its data when it has no message), must contain.</summary>
    public string? Contains { get; init; }

    /// <summary>Text the line or event must not contain.</summary>
    public string? NotContains { get; init; }

    /// <summary>A regular expression the line, or the event's message or data, must match.</summary>
    public string? Regex { get; init; }

    /// <summary>A regular expression the line or event must not match.</summary>
    public string? NotRegex { get; init; }

    /// <summary>The text and regular expression parts tell upper from lower case; by default they do not.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Events only: when set, the text and regular expression parts look at the value of this named data item, not the whole message.</summary>
    public string? DataField { get; init; }

    /// <summary>Only records at or after this time (UTC for events). Lines without a time never match when this is set.</summary>
    public DateTime? After { get; init; }

    /// <summary>Only records before this time. Lines without a time never match when this is set.</summary>
    public DateTime? Before { get; init; }
}

/// <summary>A check written or imported by the user. It is stored per user and never read from what is opened.</summary>
public sealed record CustomRule
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public CustomRuleSelector Selector { get; init; } = new();

    /// <summary>Severity of the findings the rule reports.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomRuleSeverity Severity { get; init; } = CustomRuleSeverity.Warning;

    /// <summary>
    /// Title of a finding, with <c>{Name}</c>, <c>{Count}</c>, <c>{Key}</c>, <c>{File}</c>, <c>{Provider}</c>, <c>{EventId}</c>,
    /// <c>{Text}</c>, <c>{Time}</c>, <c>{From}</c>, <c>{To}</c> and <c>{Minutes}</c> filled in (the record ones from the first
    /// record of the finding). Blank gives a sensible one.
    /// </summary>
    public string? Title { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomRuleGrouping GroupBy { get; init; } = CustomRuleGrouping.All;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomRuleTrigger Trigger { get; init; } = CustomRuleTrigger.Count;

    /// <summary>The number of matches the trigger needs: at least this many (Count, Burst), or the number a file is expected to have (Missing).</summary>
    public int MinMatches { get; init; } = 1;

    /// <summary>For Burst and Gap: the length of the window, in minutes.</summary>
    public int WindowMinutes { get; init; } = 60;
}

public static class CustomRuleValidator
{
    public const int MaxNameLength = 120;
    public const int MaxWindowMinutes = 60 * 24 * 366;

    private static readonly string[] LevelNames = { "Critical", "Error", "Warning", "Information", "Verbose" };

    /// <summary>The reasons the rule cannot run; empty when it can.</summary>
    public static IReadOnlyList<string> Validate(CustomRule rule)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            problems.Add("Give the rule a name.");
        }
        else if (rule.Name.Length > MaxNameLength)
        {
            problems.Add($"The name is longer than {MaxNameLength} characters.");
        }

        var selector = rule.Selector;
        var narrowed = Has(selector.File)
            || Has(selector.Provider)
            || selector.EventIds.Count > 0
            || selector.Levels.Count > 0
            || Has(selector.Contains)
            || Has(selector.Regex)
            || selector.After is not null
            || selector.Before is not null;
        if (!narrowed)
        {
            problems.Add("Say what to look at: a file name pattern, text it contains or a regular expression, a time range (or, for events, a provider, event ids or levels), so the rule does not match everything.");
        }

        CheckRegex(selector.Regex, "The regular expression", problems);
        CheckRegex(selector.NotRegex, "The regular expression it must not match", problems);

        if (selector.After is { } after && selector.Before is { } before && after >= before)
        {
            problems.Add("The start time must be before the end time.");
        }

        foreach (var level in selector.Levels)
        {
            if (!LevelNames.Contains(level, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"'{level}' is not a level. Use {string.Join(", ", LevelNames)}.");
            }
        }

        if (rule.MinMatches < 1)
        {
            problems.Add("The number of matches must be at least 1.");
        }

        if (rule.Title is { Length: > 300 })
        {
            problems.Add("The title is longer than 300 characters.");
        }

        if (rule.GroupBy is CustomRuleGrouping.Provider or CustomRuleGrouping.EventId && selector.Kind != CustomRuleKind.Event)
        {
            problems.Add("Grouping by provider or event id only applies to events; lines have neither.");
        }

        if (rule.GroupBy == CustomRuleGrouping.Capture && !CapturesAValue(selector.Regex))
        {
            problems.Add("Grouping by a captured value needs a regular expression with a group, for example error code (?<key>0x[0-9a-f]+).");
        }

        switch (rule.Trigger)
        {
            case CustomRuleTrigger.Burst or CustomRuleTrigger.Gap:
                if (rule.WindowMinutes is < 1 or > MaxWindowMinutes)
                {
                    problems.Add($"The window must be between 1 and {MaxWindowMinutes:N0} minutes.");
                }

                if (rule.GroupBy == CustomRuleGrouping.Each)
                {
                    problems.Add("A burst or a silence is found by comparing matches, so they cannot be reported one at a time. Group them some other way.");
                }

                if (rule.Trigger == CustomRuleTrigger.Burst && rule.MinMatches < 2)
                {
                    problems.Add("A burst needs at least 2 matches.");
                }

                break;
            case CustomRuleTrigger.Missing:
                if (!Has(selector.File))
                {
                    problems.Add("To report a file with too few matches, say which files to check in 'In files named'.");
                }

                break;
        }

        return problems;
    }

    public static IReadOnlyList<string> KnownLevels => LevelNames;

    private static bool Has(string? text) => !string.IsNullOrWhiteSpace(text);

    private static void CheckRegex(string? pattern, string what, List<string> problems)
    {
        if (!Has(pattern))
        {
            return;
        }

        try
        {
            _ = new Regex(pattern!, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            problems.Add($"{what} is not valid: {ex.Message}");
        }
    }

    private static bool CapturesAValue(string? pattern)
    {
        if (!Has(pattern))
        {
            return false;
        }

        try
        {
            return new Regex(pattern!, RegexOptions.None, TimeSpan.FromSeconds(1)).GetGroupNumbers().Length > 1;
        }
        catch (ArgumentException)
        {
            return false; // reported on its own
        }
    }
}
