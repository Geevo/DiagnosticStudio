using System.Globalization;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>A ready-made rule, with what it is for, that the user can add and change.</summary>
public sealed record RuleExample(string Name, string Purpose, CustomRule Rule)
{
    /// <summary>What the rule looks at and what it reports, in one or two plain lines.</summary>
    public string Settings => CustomRuleHelp.Describe(Rule);
}

/// <summary>The help shown beside the rule editor: how rules work, tips for the boxes, and examples of the common cases.</summary>
public static class CustomRuleHelp
{
    /// <summary>Plain-language description of how a rule works.</summary>
    public static readonly string HowItWorks = Lines(
        "1. WHAT TO LOOK AT: pick files, then narrow to the events or lines inside them. Empty boxes are ignored; every filled box must match.",
        "2. WHAT TO REPORT: a severity, and optionally a title, grouping and when a group counts.",
        "3. Tick Enabled and Save to run it on every archive or folder you open. Test runs it now, without saving.");

    /// <summary>Short notes on the boxes, and a regular expression cheat sheet.</summary>
    public static readonly string Tips = Lines(
        "NAMES AND LISTS",
        "File names and providers take several patterns separated by commas. * stands for any text and ? for one character, for example  *.log, app-?.txt  or  Microsoft-Windows-*. Case never matters here.",
        "Event ids are numbers separated by commas, for example  41, 6008.",
        "",
        "TEXT",
        "Contains looks for plain text. Must not contain throws out matches that also have that text. For an event the text is its message, or its data when it has no message. Tick Match case to tell upper from lower case; otherwise they are treated alike.",
        "To look at one value of an event instead of the whole message, give its name in Data item (for example  LogonType  or  TargetUserName).",
        "",
        "REGULAR EXPRESSIONS (the advanced way to describe text)",
        "  .        any character              \\d  a digit              \\s  a space or tab",
        "  \\b       the edge of a word         ^   start of the text    $   end of the text",
        "  a|b      a or b                     (a|b)  a group           x?  x or nothing",
        "  x*       x any number of times      x+  x one or more        x{3}  x exactly 3 times",
        "  [0-9a-f] one character from a set   [^0-9]  anything but a digit",
        "  (?<key>\\d+)  a named group; with Group by 'a captured value' each distinct value gets its own finding",
        "Use \\ before a character that means something, such as \\. for a full stop. Examples:  error code (?<key>0x[0-9a-f]+)   or   \\bfail(ed|ure)?\\b",
        "",
        "WHEN TO REPORT",
        "At least N matches: report a group once it has N matches.",
        "A burst: N matches within a number of minutes of each other, for example 5 failed sign-ins in 10 minutes.",
        "A silence: two matches in a row further apart than a number of minutes, for example nothing logged for 2 hours. Needs lines or events with a time.",
        "Too few matches in a file: report a file that has fewer than N matches (so N = 1 reports a file with none). Say which files in 'In files named'.",
        "",
        "TITLES",
        "Leave the title blank for a sensible one, or use {Name} {Count} {Key} {File} {Provider} {EventId} {Text} {Time} {From} {To} {Minutes}. {From} and {To} are the first and last time. {Minutes} is how long a burst or a silence lasted.");

    /// <summary>The examples offered beside the editor.</summary>
    public static IReadOnlyList<RuleExample> Examples { get; } = new[]
    {
        new RuleExample(
            "Unexpected shutdown",
            "Reports when the computer restarted without a clean shutdown (Kernel-Power event 41). One finding for all of them, with every occurrence as evidence.",
            new CustomRule
            {
                Name = "Unexpected shutdowns",
                Description = "The computer restarted without shutting down cleanly.",
                Severity = CustomRuleSeverity.Error,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Event, File = "System.evtx", Provider = "Microsoft-Windows-Kernel-Power", EventIds = new uint[] { 41 } },
            }),
        new RuleExample(
            "Disk problems, by event",
            "Reports disk and file system errors in the System log, one finding for each kind of event (7 bad block, 11 controller error, 51 paging error, 153 retried I/O).",
            new CustomRule
            {
                Name = "Disk problems",
                Severity = CustomRuleSeverity.Error,
                Title = "Disk event {EventId} reported {Count} times",
                GroupBy = CustomRuleGrouping.EventId,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Event, File = "System.evtx", Provider = "disk", EventIds = new uint[] { 7, 11, 51, 153 } },
            }),
        new RuleExample(
            "A provider that keeps logging errors",
            "Reports any provider that logged 10 or more errors, in any event log. One finding for each provider, so a noisy component stands out.",
            new CustomRule
            {
                Name = "Providers logging many errors",
                Severity = CustomRuleSeverity.Warning,
                Title = "{Provider} logged {Count} errors",
                GroupBy = CustomRuleGrouping.Provider,
                MinMatches = 10,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Event, File = "*.evtx", Levels = new[] { "Error", "Critical" } },
            }),
        new RuleExample(
            "Errors, apart from known noise",
            "Reports error events from every provider except the ones you already know are chatty. Add more names to Leave out providers; * works here too.",
            new CustomRule
            {
                Name = "Errors apart from known noise",
                Severity = CustomRuleSeverity.Warning,
                Title = "{Provider}: {Count} errors",
                GroupBy = CustomRuleGrouping.Provider,
                Selector = new CustomRuleSelector
                {
                    Kind = CustomRuleKind.Event,
                    Levels = new[] { "Error" },
                    ExcludeProvider = "Microsoft-Windows-DistributedCOM, Microsoft-Windows-WMI*",
                },
            }),
        new RuleExample(
            "Every critical event, one by one",
            "Reports each critical event as a finding of its own, so none is lost in a count. Use it for things that are rare and always matter.",
            new CustomRule
            {
                Name = "Critical events",
                Severity = CustomRuleSeverity.Error,
                Title = "{Provider} event {EventId}: {Text}",
                GroupBy = CustomRuleGrouping.Each,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Event, Levels = new[] { "Critical" } },
            }),
        new RuleExample(
            "A phrase in event messages",
            "Reports events whose message contains \"access is denied\", one finding for each provider that wrote one.",
            new CustomRule
            {
                Name = "Access denied in events",
                Severity = CustomRuleSeverity.Warning,
                Title = "Access denied in {Provider} ({Count})",
                GroupBy = CustomRuleGrouping.Provider,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Event, Contains = "access is denied" },
            }),
        new RuleExample(
            "Failure words in text logs",
            "Reports each text log with 5 or more lines that say failed, failure, exception or fatal. One finding for each file, linked to every such line.",
            new CustomRule
            {
                Name = "Failure lines in logs",
                Severity = CustomRuleSeverity.Warning,
                Title = "{File}: {Count} failure lines",
                GroupBy = CustomRuleGrouping.File,
                MinMatches = 5,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Line, File = "*.log", Regex = @"\b(fail(ed|ure|ing)?|exception|fatal)\b" },
            }),
        new RuleExample(
            "Real ERROR lines only",
            "Matches the word ERROR in capitals (Match case), in .log and .txt files except debug logs, and skips lines that also say \"retrying\".",
            new CustomRule
            {
                Name = "Real ERROR lines",
                Severity = CustomRuleSeverity.Error,
                Title = "{File}: {Count} ERROR lines",
                GroupBy = CustomRuleGrouping.File,
                Selector = new CustomRuleSelector
                {
                    Kind = CustomRuleKind.Line,
                    File = "*.log, *.txt",
                    ExcludeFile = "*debug*",
                    Regex = @"\bERROR\b",
                    MatchCase = true,
                    NotContains = "retrying",
                },
            }),
        new RuleExample(
            "The same error code, repeated",
            "Finds lines like \"error code 0x80070005\" and reports each code that appears 3 or more times. The group named key in the regular expression is the value that is counted.",
            new CustomRule
            {
                Name = "Repeated error codes",
                Severity = CustomRuleSeverity.Error,
                Title = "Error code {Key} seen {Count} times",
                GroupBy = CustomRuleGrouping.Capture,
                MinMatches = 3,
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Line, Regex = @"error code (?<key>0x[0-9A-Fa-f]+)" },
            }),
        new RuleExample(
            "Many failed sign-ins in a short time",
            "Looks at failed sign-in events (4625) whose LogonType data item is 3 (network) or 10 (remote desktop), and reports 5 or more within 10 minutes.",
            new CustomRule
            {
                Name = "Burst of failed sign-ins",
                Severity = CustomRuleSeverity.Error,
                Trigger = CustomRuleTrigger.Burst,
                MinMatches = 5,
                WindowMinutes = 10,
                Selector = new CustomRuleSelector
                {
                    Kind = CustomRuleKind.Event,
                    File = "Security.evtx",
                    Provider = "Microsoft-Windows-Security-Auditing",
                    EventIds = new uint[] { 4625 },
                    DataField = "LogonType",
                    Regex = "^(3|10)$",
                },
            }),
        new RuleExample(
            "A long silence in a log",
            "Reports any stretch of more than 2 hours between lines in a .log file, citing the line before and the line after. Lines need a time in them.",
            new CustomRule
            {
                Name = "Silence in a log",
                Description = "A long gap can mean the service was stopped or hung.",
                Severity = CustomRuleSeverity.Warning,
                GroupBy = CustomRuleGrouping.File,
                Trigger = CustomRuleTrigger.Gap,
                WindowMinutes = 120,
                Title = "{File}: nothing logged for {Minutes} min",
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Line, File = "*.log" },
            }),
        new RuleExample(
            "An expected line is missing",
            "Reports each agent log that has no line saying \"heartbeat\". Change the file name and the text to whatever a healthy log should contain.",
            new CustomRule
            {
                Name = "No heartbeat in agent log",
                Severity = CustomRuleSeverity.Warning,
                Trigger = CustomRuleTrigger.Missing,
                MinMatches = 1,
                Title = "{File} has no heartbeat",
                Selector = new CustomRuleSelector { Kind = CustomRuleKind.Line, File = "agent*.log", Contains = "heartbeat" },
            }),
    };

    /// <summary>What a rule looks at and what it reports, in one or two plain lines.</summary>
    public static string Describe(CustomRule rule)
    {
        var culture = CultureInfo.CurrentCulture;
        var selector = rule.Selector;
        var looks = new List<string>
        {
            selector.Kind == CustomRuleKind.Event ? "event log entries" : "lines of text files",
        };
        Add(looks, selector.File, "in files named");
        Add(looks, selector.ExcludeFile, "except files named");
        Add(looks, selector.Provider, "from");
        Add(looks, selector.ExcludeProvider, "but not from");
        if (selector.EventIds.Count > 0)
        {
            looks.Add("with event id " + string.Join(", ", selector.EventIds));
        }

        if (selector.ExcludeEventIds.Count > 0)
        {
            looks.Add("but not event id " + string.Join(", ", selector.ExcludeEventIds));
        }

        if (selector.Levels.Count > 0)
        {
            looks.Add("at level " + string.Join(" or ", selector.Levels));
        }

        var of = string.IsNullOrWhiteSpace(selector.DataField) ? string.Empty : $" of {selector.DataField.Trim()}";
        Add(looks, selector.Contains, $"whose text{of} contains", quote: true);
        Add(looks, selector.NotContains, $"and does not contain", quote: true);
        Add(looks, selector.Regex, $"and text{of} matches", quote: false);
        Add(looks, selector.NotRegex, $"and does not match", quote: false);
        if (selector.After is { } after)
        {
            looks.Add("after " + after.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        if (selector.Before is { } before)
        {
            looks.Add("before " + before.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        var text = "Looks at: " + string.Join(" ", looks);
        var grouping = rule.GroupBy switch
        {
            CustomRuleGrouping.Each => "one finding for each match",
            CustomRuleGrouping.File => "one finding for each file",
            CustomRuleGrouping.Provider => "one finding for each provider",
            CustomRuleGrouping.EventId => "one finding for each event id",
            CustomRuleGrouping.Capture => "one finding for each captured value",
            _ => "one finding for all matches",
        };
        var when = rule.Trigger switch
        {
            CustomRuleTrigger.Burst => string.Create(culture, $", when {rule.MinMatches:N0} or more matches fall within {rule.WindowMinutes:N0} minutes"),
            CustomRuleTrigger.Gap => string.Create(culture, $", when there is nothing for more than {rule.WindowMinutes:N0} minutes between two matches"),
            CustomRuleTrigger.Missing => string.Create(culture, $", for each file with fewer than {rule.MinMatches:N0} matches"),
            _ => rule.MinMatches > 1 ? string.Create(culture, $", only with {rule.MinMatches:N0} or more matches") : string.Empty,
        };
        return text + Environment.NewLine + (rule.Trigger == CustomRuleTrigger.Missing
            ? $"Reports: {rule.Severity}{when}."
            : $"Reports: {rule.Severity}, {grouping}{when}.");
    }

    private static void Add(List<string> parts, string? value, string label, bool quote = false)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(quote ? $"{label} \"{value}\"" : $"{label} {value}");
        }
    }

    private static string Lines(params string[] lines) => string.Join("\r\n", lines);
}
