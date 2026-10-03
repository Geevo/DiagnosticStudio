using System.Globalization;
using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>The part of a matched record that a rule groups and reports on.</summary>
/// <param name="Ref">A short code, such as r1, standing for this exact record.</param>
/// <param name="Time">When it happened; for an event this is UTC, for a line it is as written in the file.</param>
public sealed record RuleRecord(string Ref, string File, string? Provider, uint? EventId, string Text, DateTime? Time, bool IsUtc = false)
{
    /// <summary>The time as shown in a finding, or null when the record has none.</summary>
    public string? TimeText => Time?.ToString(IsUtc ? "yyyy-MM-dd HH:mm:ss 'UTC'" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>A file a rule looked in, and how many records in it matched. Needed to say that something is missing from a file.</summary>
public sealed record ExaminedFile(string Name, DiagnosticLocation Location, int Matches);

/// <summary>The records a rule matched, with what each record's <c>Ref</c> stands for.</summary>
public sealed class RuleInput
{
    /// <summary>The matched records, in the order they were found.</summary>
    public required IReadOnlyList<RuleRecord> Records { get; init; }

    public required IReadOnlyDictionary<string, DiagnosticLocation> Locations { get; init; }

    /// <summary>Short description of each record, shown as the evidence text.</summary>
    public required IReadOnlyDictionary<string, string> Descriptions { get; init; }

    /// <summary>Every file the rule looked in, including those with no match.</summary>
    public required IReadOnlyList<ExaminedFile> Files { get; init; }

    public int Count => Records.Count;

    /// <summary>More records matched than the limit allows; the rest were counted but not kept.</summary>
    public bool Truncated { get; init; }

    public int Matched { get; init; }
}

/// <summary>Chooses the records of a rule from the opened files, using the rule's selector.</summary>
public sealed class RuleInputBuilder
{
    /// <summary>Records kept for one rule. Beyond this the rule should be narrowed.</summary>
    public const int MaxRecords = 50_000;

    private const int MaxTextLength = 4000;

    private readonly IDocumentLoader _loader;

    public RuleInputBuilder(IDocumentLoader loader)
    {
        _loader = loader;
    }

    public async Task<RuleInput> BuildAsync(CustomRule rule, IReadOnlyList<DiagnosticArtifact> artifacts, CancellationToken cancellationToken)
    {
        var selector = rule.Selector;
        var plain = new List<RuleRecord>();
        var locations = new Dictionary<string, DiagnosticLocation>(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<ExaminedFile>();
        var matched = 0;

        var include = Wildcards.Parse(selector.File);
        var exclude = Wildcards.Parse(selector.ExcludeFile);
        var wanted = artifacts
            .Where(a => !a.IsContainer
                && (include.Length == 0 || Wildcards.Any(include, a.Name))
                && !Wildcards.Any(exclude, a.Name))
            .Where(a => selector.Kind == CustomRuleKind.Event ? a.ArtifactType == ArtifactType.EventLog : a.ArtifactType != ArtifactType.EventLog)
            .ToList();

        var text = TextFilter.From(selector);
        foreach (var artifact in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentLoadResult loaded;
            try
            {
                loaded = await _loader.LoadAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue; // an unreadable file is reported by the file checks, not by every rule
            }

            int found;
            if (selector.Kind == CustomRuleKind.Event)
            {
                if (loaded.Document is not EventLogDocument events)
                {
                    continue;
                }

                found = AddEvents(selector, text, artifact, events, plain, locations, descriptions, cancellationToken);
            }
            else if (DocumentText.LinesOf(loaded.Document) is { } lines)
            {
                found = AddLines(selector, text, artifact, lines, plain, locations, descriptions, cancellationToken);
            }
            else
            {
                continue;
            }

            matched += found;
            files.Add(new ExaminedFile(artifact.Name, DiagnosticLocation.ForArtifact(artifact.Id), found));
        }

        return new RuleInput
        {
            Records = plain,
            Locations = locations,
            Descriptions = descriptions,
            Files = files,
            Truncated = matched > plain.Count,
            Matched = matched,
        };
    }

    private static int AddEvents(
        CustomRuleSelector selector,
        TextFilter? text,
        DiagnosticArtifact artifact,
        EventLogDocument document,
        List<RuleRecord> plain,
        Dictionary<string, DiagnosticLocation> locations,
        Dictionary<string, string> descriptions,
        CancellationToken token)
    {
        var source = document.Source;

        HashSet<ushort>? providerIds = null;
        var includeProviders = Wildcards.Parse(selector.Provider);
        var excludeProviders = Wildcards.Parse(selector.ExcludeProvider);
        if (includeProviders.Length > 0 || excludeProviders.Length > 0)
        {
            providerIds = source.Providers
                .Where(p => (includeProviders.Length == 0 || Wildcards.Any(includeProviders, p.Name)) && !Wildcards.Any(excludeProviders, p.Name))
                .Select(p => p.Id)
                .ToHashSet();
            if (providerIds.Count == 0)
            {
                return 0;
            }
        }

        var ids = selector.EventIds.Count == 0 ? null : selector.EventIds.ToHashSet();
        var skipIds = selector.ExcludeEventIds.Count == 0 ? null : selector.ExcludeEventIds.ToHashSet();
        var levels = selector.Levels.Count == 0
            ? null
            : selector.Levels.Select(LevelNumber).Where(n => n >= 0).Select(n => (byte)n).ToHashSet();
        var after = selector.After?.Ticks;
        var before = selector.Before?.Ticks;

        var matched = 0;
        for (var i = 0; i < source.Count; i++)
        {
            if ((i & 0x3FFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var summary = source.GetSummary(i);
            if ((providerIds is not null && !providerIds.Contains(summary.ProviderId))
                || (ids is not null && !ids.Contains(summary.EventId))
                || (skipIds is not null && skipIds.Contains(summary.EventId))
                || (levels is not null && !levels.Contains(summary.Level))
                || (after is not null && summary.TimeUtc.Ticks < after)
                || (before is not null && summary.TimeUtc.Ticks >= before))
            {
                continue;
            }

            // The message is only read for events that can still match, or when there is a text to look for.
            if (text is null && plain.Count >= MaxRecords)
            {
                matched++;
                continue; // keep counting so the shortfall can be reported
            }

            var detail = source.ReadDetail(i);
            var dataText = string.Join(" | ", detail.Data.Select(d => d.Value));
            var shown = detail.Message ?? dataText;
            if (text is not null)
            {
                var looked = selector.DataField is { Length: > 0 } field
                    ? detail.Data.FirstOrDefault(d => string.Equals(d.Name, field.Trim(), StringComparison.OrdinalIgnoreCase))?.Value
                    : shown;
                if (!text.Matches(looked))
                {
                    continue;
                }
            }

            matched++;
            if (plain.Count >= MaxRecords)
            {
                continue;
            }

            var reference = "r" + (plain.Count + 1).ToString(CultureInfo.InvariantCulture);
            plain.Add(new RuleRecord(reference, artifact.Name, detail.Provider, summary.EventId, Truncate(shown), summary.TimeUtc, IsUtc: true));
            locations[reference] = DiagnosticLocation.ForEventRecord(artifact.Id, summary.RecordId);
            descriptions[reference] = $"{artifact.Name} · event {summary.EventId} · {summary.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}";
        }

        return matched;
    }

    private static int AddLines(
        CustomRuleSelector selector,
        TextFilter? text,
        DiagnosticArtifact artifact,
        ITextLineSource lines,
        List<RuleRecord> plain,
        Dictionary<string, DiagnosticLocation> locations,
        Dictionary<string, string> descriptions,
        CancellationToken token)
    {
        var after = selector.After?.Ticks;
        var before = selector.Before?.Ticks;
        var timed = after is not null || before is not null;
        var matched = 0;
        var lineNumber = 0;
        foreach (var line in lines.EnumerateLines())
        {
            lineNumber++;
            if ((lineNumber & 0xFFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (text is not null && !text.Matches(line))
            {
                continue;
            }

            DateTime? time = null;
            var needsTime = timed || plain.Count < MaxRecords;
            if (needsTime)
            {
                time = LogLineAnalyzer.Analyze(line).Timestamp;
                if (timed && (time is null || (after is not null && time.Value.Ticks < after) || (before is not null && time.Value.Ticks >= before)))
                {
                    continue;
                }
            }

            matched++;
            if (plain.Count >= MaxRecords)
            {
                continue;
            }

            var reference = "r" + (plain.Count + 1).ToString(CultureInfo.InvariantCulture);
            plain.Add(new RuleRecord(reference, artifact.Name, null, null, Truncate(line), time));
            locations[reference] = DiagnosticLocation.ForLine(artifact.Id, lineNumber);
            descriptions[reference] = $"{artifact.Name} · line {lineNumber.ToString("N0", CultureInfo.InvariantCulture)}";
        }

        return matched;
    }

    /// <summary>The "contains" and "matches" parts of a selector, applied to the text of a line or an event.</summary>
    private sealed class TextFilter
    {
        private readonly string? _contains;
        private readonly string? _notContains;
        private readonly Regex? _regex;
        private readonly Regex? _notRegex;
        private readonly StringComparison _comparison;

        private TextFilter(string? contains, string? notContains, Regex? regex, Regex? notRegex, StringComparison comparison)
        {
            _contains = contains;
            _notContains = notContains;
            _regex = regex;
            _notRegex = notRegex;
            _comparison = comparison;
        }

        public static TextFilter? From(CustomRuleSelector selector)
        {
            var contains = Blank(selector.Contains);
            var notContains = Blank(selector.NotContains);
            var options = RegexOptions.CultureInvariant | (selector.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = Blank(selector.Regex) is { } r ? new Regex(r, options, TimeSpan.FromSeconds(1)) : null;
            var notRegex = Blank(selector.NotRegex) is { } n ? new Regex(n, options, TimeSpan.FromSeconds(1)) : null;
            return contains is null && notContains is null && regex is null && notRegex is null
                ? null
                : new TextFilter(contains, notContains, regex, notRegex, selector.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A null text (a data item the event does not have) can never contain or match anything.</summary>
        public bool Matches(string? text)
        {
            if (_contains is not null && (text is null || !text.Contains(_contains, _comparison)))
            {
                return false;
            }

            if (_notContains is not null && text is not null && text.Contains(_notContains, _comparison))
            {
                return false;
            }

            try
            {
                if (_regex is not null && (text is null || !_regex.IsMatch(text)))
                {
                    return false;
                }

                return _notRegex is null || text is null || !_notRegex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Truncate(string text) =>
        text.Length > MaxTextLength ? text[..MaxTextLength] : text;

    private static int LevelNumber(string name) => name.Trim().ToLowerInvariant() switch
    {
        "critical" => EventLevels.Critical,
        "error" => EventLevels.Error,
        "warning" => EventLevels.Warning,
        "information" => EventLevels.Information,
        "verbose" => EventLevels.Verbose,
        _ => -1,
    };

    /// <summary>Whether <paramref name="artifact"/> is named like one of the comma separated <paramref name="patterns"/> (<c>*</c> and <c>?</c>); an empty list matches all.</summary>
    internal static bool FileMatches(DiagnosticArtifact artifact, string? patterns)
    {
        var parsed = Wildcards.Parse(patterns);
        return parsed.Length == 0 || Wildcards.Any(parsed, artifact.Name);
    }
}

/// <summary>Comma separated names with <c>*</c> and <c>?</c>, matched without regard to case.</summary>
internal static class Wildcards
{
    private static readonly char[] Separators = { ',', ';', '\r', '\n' };

    public static Regex[] Parse(string? patterns) =>
        string.IsNullOrWhiteSpace(patterns)
            ? Array.Empty<Regex>()
            : patterns.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => new Regex(
                    "^" + Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1)))
                .ToArray();

    public static bool Any(Regex[] patterns, string name)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(name))
            {
                return true;
            }
        }

        return false;
    }
}
