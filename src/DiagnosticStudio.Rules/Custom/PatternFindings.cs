using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Findings;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>
/// Turns the records a rule matched into findings: the records are grouped as the rule says, and the rule's trigger
/// decides which groups are worth a finding (enough matches, a burst, a silence, or a file with too few). The evidence
/// is always the records themselves.
/// </summary>
internal static class PatternFindings
{
    private const int MaxTitleText = 120;

    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>What a finding is about; the title and the description are written from it.</summary>
    private sealed record Subject(string Key, IReadOnlyList<RuleRecord> Records, string Identity, double? Minutes = null, string? From = null, string? To = null);

    public static IReadOnlyList<Finding> Build(CustomRule rule, RuleInput input, List<string> problems)
    {
        var subjects = rule.Trigger switch
        {
            CustomRuleTrigger.Missing => MissingFiles(rule, input),
            CustomRuleTrigger.Burst => Bursts(rule, Groups(rule, input)),
            CustomRuleTrigger.Gap => Gaps(rule, Groups(rule, input)),
            _ => Counted(rule, Groups(rule, input)),
        };

        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            if (findings.Count >= CustomRuleEngine.MaxFindings)
            {
                problems.Add($"The rule produced more than {CustomRuleEngine.MaxFindings} findings; the rest were dropped. Group the matches or narrow the rule.");
                break;
            }

            var finding = rule.Trigger == CustomRuleTrigger.Missing ? ReportMissing(rule, input, subject) : Report(rule, input, subject);
            if (seen.Add(finding.Id))
            {
                findings.Add(finding);
            }
        }

        return findings;
    }

    private static List<(string Key, List<RuleRecord> Records)> Groups(CustomRule rule, RuleInput input)
    {
        var groups = new List<(string Key, List<RuleRecord> Records)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var capture = rule.GroupBy == CustomRuleGrouping.Capture && !string.IsNullOrWhiteSpace(rule.Selector.Regex)
            ? new Regex(rule.Selector.Regex, RegexOptions.CultureInvariant | (rule.Selector.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(1))
            : null;

        foreach (var record in input.Records)
        {
            var key = KeyOf(rule.GroupBy, record, capture);
            if (key is null)
            {
                continue;
            }

            if (!byKey.TryGetValue(key, out var at))
            {
                at = groups.Count;
                byKey[key] = at;
                groups.Add((key, new List<RuleRecord>()));
            }

            groups[at].Records.Add(record);
        }

        return groups;
    }

    private static IEnumerable<Subject> Counted(CustomRule rule, List<(string Key, List<RuleRecord> Records)> groups)
    {
        foreach (var (key, records) in groups)
        {
            if (records.Count >= Math.Max(1, rule.MinMatches))
            {
                yield return new Subject(key, records, rule.GroupBy == CustomRuleGrouping.Each ? records[0].Ref : key, From: FirstTime(records), To: LastTime(records));
            }
        }
    }

    /// <summary>At least MinMatches records inside one window; a stretch is reported once, then the search carries on after it.</summary>
    private static IEnumerable<Subject> Bursts(CustomRule rule, List<(string Key, List<RuleRecord> Records)> groups)
    {
        var window = TimeSpan.FromMinutes(rule.WindowMinutes);
        var needed = Math.Max(2, rule.MinMatches);
        foreach (var (key, records) in groups)
        {
            var timed = Timed(records);
            var start = 0;
            while (start < timed.Count)
            {
                var end = start;
                while (end + 1 < timed.Count && timed[end + 1].Time!.Value - timed[start].Time!.Value <= window)
                {
                    end++;
                }

                if (end - start + 1 >= needed)
                {
                    var burst = timed.GetRange(start, end - start + 1);
                    var span = burst[^1].Time!.Value - burst[0].Time!.Value;
                    yield return new Subject(key, burst, key + "|" + burst[0].Time!.Value.Ticks.ToString(CultureInfo.InvariantCulture), span.TotalMinutes, burst[0].TimeText, burst[^1].TimeText);
                    start = end + 1;
                }
                else
                {
                    start++;
                }
            }
        }
    }

    /// <summary>Two matches in a row further apart than the window; the finding cites the record before and the one after.</summary>
    private static IEnumerable<Subject> Gaps(CustomRule rule, List<(string Key, List<RuleRecord> Records)> groups)
    {
        var window = TimeSpan.FromMinutes(rule.WindowMinutes);
        foreach (var (key, records) in groups)
        {
            var timed = Timed(records);
            for (var i = 1; i < timed.Count; i++)
            {
                var gap = timed[i].Time!.Value - timed[i - 1].Time!.Value;
                if (gap > window)
                {
                    yield return new Subject(
                        key,
                        new[] { timed[i - 1], timed[i] },
                        key + "|" + timed[i - 1].Time!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
                        gap.TotalMinutes,
                        timed[i - 1].TimeText,
                        timed[i].TimeText);
                }
            }
        }
    }

    private static IEnumerable<Subject> MissingFiles(CustomRule rule, RuleInput input)
    {
        var expected = Math.Max(1, rule.MinMatches);
        foreach (var file in input.Files)
        {
            if (file.Matches < expected)
            {
                yield return new Subject(file.Name, Array.Empty<RuleRecord>(), file.Location.ToString(), Minutes: file.Matches);
            }
        }
    }

    private static List<RuleRecord> Timed(List<RuleRecord> records) =>
        records.Where(r => r.Time is not null).OrderBy(r => r.Time).ToList();

    private static string? FirstTime(IReadOnlyList<RuleRecord> records) => records.Where(r => r.Time is not null).MinBy(r => r.Time)?.TimeText;

    private static string? LastTime(IReadOnlyList<RuleRecord> records) => records.Where(r => r.Time is not null).MaxBy(r => r.Time)?.TimeText;

    private static string? KeyOf(CustomRuleGrouping grouping, RuleRecord record, Regex? capture)
    {
        switch (grouping)
        {
            case CustomRuleGrouping.All:
                return string.Empty;
            case CustomRuleGrouping.Each:
                return record.Ref;
            case CustomRuleGrouping.File:
                return record.File;
            case CustomRuleGrouping.Provider:
                return record.Provider ?? "(no provider)";
            case CustomRuleGrouping.EventId:
                return record.EventId?.ToString(CultureInfo.InvariantCulture) ?? "(none)";
            default:
                try
                {
                    var match = capture?.Match(record.Text);
                    if (match is not { Success: true })
                    {
                        return null;
                    }

                    var named = match.Groups["key"];
                    var group = named.Success ? named : match.Groups.Count > 1 ? match.Groups[1] : null;
                    return group is { Success: true, Value.Length: > 0 } ? group.Value : null;
                }
                catch (RegexMatchTimeoutException)
                {
                    return null;
                }
        }
    }

    private static Finding Report(CustomRule rule, RuleInput input, Subject subject)
    {
        var records = subject.Records;
        var evidence = new List<FindingEvidence>();
        foreach (var record in records.Take(CustomRuleEngine.MaxEvidence))
        {
            var where = input.Descriptions.TryGetValue(record.Ref, out var d) ? d : record.File;
            evidence.Add(new FindingEvidence
            {
                Location = input.Locations[record.Ref],
                Description = string.IsNullOrWhiteSpace(record.Text) ? where : $"{where} · {Shorten(OneLine(record.Text), 160)}",
            });
        }

        return new Finding
        {
            Id = $"custom:{rule.Id}:{Fingerprint(rule.Trigger + "|" + rule.GroupBy + "|" + subject.Identity)}",
            Severity = SeverityOf(rule),
            Title = Shorten(Fill(rule, subject), CustomRuleEngine.MaxTitleLength),
            Description = Summary(rule, subject),
            Evidence = evidence,
            Tags = new[] { "custom rule" },
            Notes = $"Custom rule “{rule.Name}”.",
        };
    }

    private static Finding ReportMissing(CustomRule rule, RuleInput input, Subject subject)
    {
        var file = input.Files.First(f => f.Location.ToString() == subject.Identity);
        var expected = Math.Max(1, rule.MinMatches);
        return new Finding
        {
            Id = $"custom:{rule.Id}:{Fingerprint(rule.Trigger + "|" + subject.Identity)}",
            Severity = SeverityOf(rule),
            Title = Shorten(Fill(rule, subject), CustomRuleEngine.MaxTitleLength),
            Description = Describe(rule, string.Create(CultureInfo.CurrentCulture, $"{file.Name} has {file.Matches:N0} matching records; at least {expected:N0} {(expected == 1 ? "was" : "were")} expected.")),
            Evidence = new[]
            {
                new FindingEvidence
                {
                    Location = file.Location,
                    Description = string.Create(CultureInfo.CurrentCulture, $"{file.Name} · {file.Matches:N0} matching"),
                },
            },
            Tags = new[] { "custom rule" },
            Notes = $"Custom rule “{rule.Name}”.",
        };
    }

    private static FindingSeverity SeverityOf(CustomRule rule) => rule.Severity switch
    {
        CustomRuleSeverity.Error => FindingSeverity.Error,
        CustomRuleSeverity.Information => FindingSeverity.Information,
        _ => FindingSeverity.Warning,
    };

    private static string Fill(CustomRule rule, Subject subject)
    {
        var records = subject.Records;
        var first = records.Count > 0 ? records[0] : null;
        var template = string.IsNullOrWhiteSpace(rule.Title) ? DefaultTitle(rule, subject) : rule.Title;
        return Placeholder.Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "name" => rule.Name,
            "count" => (rule.Trigger == CustomRuleTrigger.Missing ? (int)(subject.Minutes ?? 0) : records.Count).ToString("N0", CultureInfo.CurrentCulture),
            "key" => subject.Key,
            "file" => first?.File ?? (rule.Trigger == CustomRuleTrigger.Missing ? subject.Key : string.Empty),
            "provider" => first?.Provider ?? string.Empty,
            "eventid" => first?.EventId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "text" => first is null ? string.Empty : Shorten(OneLine(first.Text), MaxTitleText),
            "time" => first?.TimeText ?? string.Empty,
            "from" => subject.From ?? string.Empty,
            "to" => subject.To ?? string.Empty,
            "minutes" => rule.Trigger == CustomRuleTrigger.Missing || subject.Minutes is null ? string.Empty : Minutes(subject.Minutes.Value),
            _ => match.Value,
        });
    }

    private static string DefaultTitle(CustomRule rule, Subject subject)
    {
        var keyed = rule.GroupBy is not (CustomRuleGrouping.All or CustomRuleGrouping.Each);
        return rule.Trigger switch
        {
            CustomRuleTrigger.Missing => "{Name}: {File} ({Count} found)",
            CustomRuleTrigger.Burst => keyed ? "{Name}: {Key}, {Count} within {Minutes} min" : "{Name}: {Count} within {Minutes} min",
            CustomRuleTrigger.Gap => keyed ? "{Name}: {Key}, nothing for {Minutes} min" : "{Name}: nothing for {Minutes} min",
            _ => rule.GroupBy switch
            {
                CustomRuleGrouping.All => "{Name} ({Count})",
                CustomRuleGrouping.Each => "{Name}: {Text}",
                _ => "{Name}: {Key} ({Count})",
            },
        };
    }

    private static string Summary(CustomRule rule, Subject subject)
    {
        var culture = CultureInfo.CurrentCulture;
        var records = subject.Records;
        string text;
        switch (rule.Trigger)
        {
            case CustomRuleTrigger.Gap:
                text = $"No matching record for {Minutes(subject.Minutes ?? 0)} minutes, from {subject.From} to {subject.To}.";
                break;
            case CustomRuleTrigger.Burst:
                text = string.Create(culture, $"{records.Count:N0} matching records within {Minutes(subject.Minutes ?? 0)} minutes, from {subject.From} to {subject.To}.");
                break;
            default:
                var counted = records.Count == 1
                    ? "1 matching record"
                    : string.Create(culture, $"{records.Count:N0} matching records");
                var when = subject.From is null ? string.Empty
                    : subject.From == subject.To ? $", at {subject.From}"
                    : $", first at {subject.From}, last at {subject.To}";
                text = counted + when + ".";
                break;
        }

        return Describe(rule, text);
    }

    private static string Describe(CustomRule rule, string text) =>
        string.IsNullOrWhiteSpace(rule.Description) ? text : Shorten(rule.Description.Trim(), 1000) + " " + text;

    private static string Minutes(double minutes) =>
        minutes < 10 ? Math.Round(minutes, 1).ToString("0.#", CultureInfo.InvariantCulture) : Math.Round(minutes).ToString("N0", CultureInfo.CurrentCulture);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
}
