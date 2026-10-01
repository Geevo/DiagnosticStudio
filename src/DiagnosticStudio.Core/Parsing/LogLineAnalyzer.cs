using System.Globalization;
using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Core.Parsing;

/// <summary>
/// Cheap, explainable per-line recognition of severity and timestamps. It makes no attempt to
/// understand log formats; it recognises a few common conventions and says nothing otherwise.
/// </summary>
public static partial class LogLineAnalyzer
{
    // Level tokens are only trusted near the start of the line, where logging frameworks put them.
    private const int SeverityScanLength = 120;

    public static LogLineInfo Analyze(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return LogLineInfo.None;
        }

        if (line.Contains("<![LOG[", StringComparison.Ordinal))
        {
            return AnalyzeCmTrace(line);
        }

        var (start, length, timestamp) = FindTimestamp(line);
        return new LogLineInfo(FindSeverity(line), timestamp, start, length);
    }

    private static LogLineInfo AnalyzeCmTrace(string line)
    {
        var severity = LogSeverity.None;
        var typeMatch = CmTraceType().Match(line);
        if (typeMatch.Success)
        {
            severity = typeMatch.Groups[1].Value switch
            {
                "1" => LogSeverity.Information,
                "2" => LogSeverity.Warning,
                "3" => LogSeverity.Error,
                _ => LogSeverity.None,
            };
        }

        // <... time="14:12:00.123+000" date="7-23-2026" ...> : CMTrace always writes M-d-yyyy.
        DateTime? timestamp = null;
        var time = CmTraceTime().Match(line);
        var date = CmTraceDate().Match(line);
        if (time.Success && date.Success
            && DateTime.TryParseExact(
                date.Groups[1].Value + " " + time.Groups[1].Value,
                new[] { "M-d-yyyy HH:mm:ss.FFFFFFF", "M-d-yyyy HH:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            timestamp = parsed;
        }

        var span = time.Success ? time.Groups[1] : null;
        return new LogLineInfo(severity, timestamp, span?.Index ?? -1, span?.Length ?? 0);
    }

    private static (int Start, int Length, DateTime? Value) FindTimestamp(string line)
    {
        var iso = IsoTimestamp().Match(line);
        if (iso.Success)
        {
            var text = iso.Value.Replace(',', '.');
            DateTime? value = null;
            if (HasExplicitOffset(text))
            {
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
                {
                    value = withOffset.UtcDateTime;
                }
            }
            else if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                value = local;
            }

            return (iso.Index, iso.Length, value);
        }

        // m/d/yyyy vs d/m/yyyy cannot be told apart in general, so these are highlighted but not parsed.
        var slash = SlashTimestamp().Match(line);
        return slash.Success ? (slash.Index, slash.Length, null) : (-1, 0, null);
    }

    private static bool HasExplicitOffset(string text) =>
        text.EndsWith('Z') || text.EndsWith('z') || OffsetSuffix().IsMatch(text);

    private static LogSeverity FindSeverity(string line)
    {
        var window = line.Length > SeverityScanLength ? line[..SeverityScanLength] : line;

        var caps = UpperLevel().Match(window);
        if (caps.Success)
        {
            return Map(caps.Groups[1].Value);
        }

        var bracketed = BracketedLevel().Match(window);
        return bracketed.Success ? Map(bracketed.Groups[1].Value) : LogSeverity.None;
    }

    private static LogSeverity Map(string token) => token.ToUpperInvariant() switch
    {
        "FATAL" or "CRITICAL" or "ERROR" or "ERR" => LogSeverity.Error,
        "WARN" or "WARNING" => LogSeverity.Warning,
        "INFO" or "INFORMATION" => LogSeverity.Information,
        "DEBUG" or "TRACE" or "VERBOSE" => LogSeverity.Debug,
        _ => LogSeverity.None,
    };

    [GeneratedRegex(@"(?<![A-Za-z])(FATAL|CRITICAL|ERROR|WARNING|WARN|INFORMATION|INFO|DEBUG|TRACE|VERBOSE)(?![A-Za-z])")]
    private static partial Regex UpperLevel();

    [GeneratedRegex(@"[\[<(](Fatal|Critical|Error|Err|Warning|Warn|Information|Info|Debug|Trace|Verbose)[\]>)]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedLevel();

    [GeneratedRegex(@"\btype=""(\d)""")]
    private static partial Regex CmTraceType();

    [GeneratedRegex(@"\btime=""([^""]+?)(?:[+-]\d+)?""")]
    private static partial Regex CmTraceTime();

    [GeneratedRegex(@"\bdate=""([^""]+)""")]
    private static partial Regex CmTraceDate();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"\d{1,2}/\d{1,2}/\d{4},?\s+\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:\s?[AaPp][Mm])?")]
    private static partial Regex SlashTimestamp();

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex OffsetSuffix();
}
