using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.Rules.Rules;

/// <summary>
/// Error-level lines in a text log that repeat. A line is an error only when the log line analyzer recognises an
/// explicit level token (ERROR, FATAL, CRITICAL, or a CMTrace error type); lines are considered the same when they
/// match once timestamps, numbers, hex values and GUIDs are blanked out.
/// </summary>
public sealed partial class RepeatedLogErrorRule : IDocumentRule
{
    public const string RuleId = "repeated-log-error";

    /// <summary>Occurrences needed before a message counts as repeated.</summary>
    public const int MinOccurrences = 3;

    private const int MaxFindingsPerLog = 10;
    private const int MaxDistinctSignatures = 5000;
    private const int MaxSignatureLength = 160;
    private const int SampleLength = 200;

    public string Id => RuleId;

    public bool AppliesTo(DiagnosticArtifact artifact) => artifact.ArtifactType == ArtifactType.TextLog;

    public IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken)
    {
        if (document is not TextDocument text)
        {
            yield break;
        }

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var lineNumber = 0;
        var totalErrors = 0;

        foreach (var line in text.Lines.EnumerateLines())
        {
            lineNumber++;
            if ((lineNumber & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!MightBeError(line))
            {
                continue;
            }

            var info = LogLineAnalyzer.Analyze(line);
            if (info.Severity != LogSeverity.Error)
            {
                continue;
            }

            totalErrors++;
            var signature = Signature(line, info);
            if (!groups.TryGetValue(signature, out var group))
            {
                if (groups.Count >= MaxDistinctSignatures)
                {
                    continue;
                }

                groups[signature] = group = new Group(line.Length > SampleLength ? line[..SampleLength] + "…" : line);
            }

            group.Add(lineNumber, info.Timestamp);
        }

        var repeated = groups.Values
            .Where(g => g.Count >= MinOccurrences)
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.FirstLine)
            .ToList();

        foreach (var group in repeated.Take(MaxFindingsPerLog))
        {
            var range = group.First is { } a && group.Last is { } b && a != b
                ? $" between {a:yyyy-MM-dd HH:mm:ss} and {b:yyyy-MM-dd HH:mm:ss} (times as written in the log)"
                : string.Empty;

            yield return new Finding
            {
                Id = RuleSupport.Id(RuleId, artifact.Id, "line-" + group.FirstLine),
                Severity = FindingSeverity.Warning,
                Title = $"Repeated error in {artifact.Name}: {group.Count:N0} occurrences",
                Description = $"The same error line appears {group.Count:N0} times{range}, first at line {group.FirstLine:N0}: {group.Sample}",
                Evidence = group.Lines.Select(n => new FindingEvidence
                {
                    Location = DiagnosticLocation.ForLine(artifact.Id, n),
                    Description = "Line " + n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                }).ToList(),
                Tags = new[] { "log", "error" },
                Notes = group.Count > Group.MaxLines
                    ? $"The first {Group.MaxLines} of {group.Count:N0} lines are listed as evidence."
                    : null,
            };
        }

        if (repeated.Count > MaxFindingsPerLog)
        {
            var rest = repeated.Skip(MaxFindingsPerLog).ToList();
            yield return new Finding
            {
                Id = RuleSupport.Id(RuleId, artifact.Id, "more"),
                Severity = FindingSeverity.Information,
                Title = $"{rest.Count:N0} more repeated error patterns in {artifact.Name}",
                Description = $"Only the {MaxFindingsPerLog} most frequent repeated errors are listed; {rest.Count:N0} further patterns "
                              + $"({rest.Sum(g => g.Count):N0} lines) are not. The log contains {totalErrors:N0} error lines in all.",
                Evidence = new[]
                {
                    new FindingEvidence
                    {
                        Location = DiagnosticLocation.ForLine(artifact.Id, rest[0].FirstLine),
                        Description = "First line of the next most frequent pattern",
                    },
                },
                Tags = new[] { "log", "error" },
            };
        }
    }

    // Cheap prefilter so the analyzer's regexes only run on lines that can possibly be errors.
    private static bool MightBeError(string line) =>
        line.Contains("err", StringComparison.OrdinalIgnoreCase)
        || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
        || line.Contains("critical", StringComparison.OrdinalIgnoreCase)
        || line.Contains("type=\"3\"", StringComparison.Ordinal);

    private static string Signature(string line, LogLineInfo info)
    {
        var text = info.HasTimestampSpan && info.TimestampStart + info.TimestampLength <= line.Length
            ? line.Remove(info.TimestampStart, info.TimestampLength)
            : line;
        text = GuidPattern().Replace(text, "<guid>");
        text = HexPattern().Replace(text, "<hex>");
        text = DigitsPattern().Replace(text, "<n>");
        text = SpacesPattern().Replace(text, " ").Trim();
        return text.Length > MaxSignatureLength ? text[..MaxSignatureLength] : text;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"0[xX][0-9a-fA-F]+")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesPattern();

    private sealed class Group
    {
        public const int MaxLines = 20;

        private readonly List<int> _lines = new();

        public Group(string sample) => Sample = sample;

        public string Sample { get; }
        public int Count { get; private set; }
        public int FirstLine => _lines[0];
        public IReadOnlyList<int> Lines => _lines;
        public DateTime? First { get; private set; }
        public DateTime? Last { get; private set; }

        public void Add(int line, DateTime? timestamp)
        {
            Count++;
            if (_lines.Count < MaxLines)
            {
                _lines.Add(line);
            }

            if (timestamp is { } t)
            {
                First ??= t;
                Last = t;
            }
        }
    }
}
