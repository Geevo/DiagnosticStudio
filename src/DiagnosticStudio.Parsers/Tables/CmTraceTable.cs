using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers.Tables;

/// <summary>
/// Reads CMTrace-format logs (Configuration Manager, Intune Management Extension and many Microsoft agents): records
/// written as <c>&lt;![LOG[message]LOG]!&gt;&lt;time="14:12:00.123+000" date="7-23-2026" component="AppWorkload"
/// context="" type="1" thread="17" file=""&gt;</c>, where the message may run over several lines. The record is found
/// by its markers, never by guessing, and lines that belong to no record are counted and left to the raw view.
/// </summary>
public sealed partial class CmTraceTable : ITableSource
{
    public const int MaxLinesPerRecord = 20_000;

    private const string StartMarker = "<![LOG[";
    private const string EndMarker = "]LOG]!>";

    private static readonly TableColumn[] ColumnList =
    {
        new("Time", TableColumnKind.Time, 170),
        new("Level", TableColumnKind.Level, 90),
        new("Component", TableColumnKind.Text, 150),
        new("Thread", TableColumnKind.Number, 70),
        new("Message", TableColumnKind.Text, 900),
    };

    private readonly ITextLineSource _lines;
    private readonly int[] _first;
    private readonly int[] _last;
    private readonly byte[] _severity;

    private CmTraceTable(ITextLineSource lines, int[] first, int[] last, byte[] severity, int unreadLines, int firstUnread)
    {
        _lines = lines;
        _first = first;
        _last = last;
        _severity = severity;
        UnreadLines = unreadLines;
        FirstUnreadLine = firstUnread;
    }

    public IReadOnlyList<TableColumn> Columns => ColumnList;

    public int RowCount => _first.Length;

    public int HeaderLines => 0;

    public bool HasLevels => true;

    /// <summary>Lines that are not part of any record.</summary>
    public int UnreadLines { get; }

    public int FirstUnreadLine { get; }

    /// <summary>True when the first lines of <paramref name="text"/> hold at least one complete CMTrace record.</summary>
    public static bool LooksLikeCmTrace(string text)
    {
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        return start >= 0 && text.IndexOf(EndMarker, start, StringComparison.Ordinal) > start;
    }

    /// <summary>Finds where each record starts and ends, in one pass over the lines.</summary>
    public static CmTraceTable Build(ITextLineSource lines, CancellationToken cancellationToken = default)
    {
        var first = new List<int>();
        var last = new List<int>();
        var severity = new List<byte>();
        var unread = 0;
        var firstUnread = 0;

        var number = 0;
        var open = 0;      // line where an unfinished record began, 0 when none
        foreach (var line in lines.EnumerateLines())
        {
            number++;
            if ((number & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (open == 0)
            {
                var start = line.IndexOf(StartMarker, StringComparison.Ordinal);
                if (start < 0)
                {
                    Unread(number, ref unread, ref firstUnread);
                    continue;
                }

                if (line.IndexOf(EndMarker, start, StringComparison.Ordinal) >= 0)
                {
                    Add(first, last, severity, number, number, line);
                }
                else
                {
                    open = number;
                }

                continue;
            }

            // Inside a record. A line that starts another record means the open one never closed.
            var startsNew = line.Contains(StartMarker, StringComparison.Ordinal);
            if (!startsNew && line.Contains(EndMarker, StringComparison.Ordinal))
            {
                Add(first, last, severity, open, number, line);
                open = 0;
            }
            else if (startsNew || number - open >= MaxLinesPerRecord)
            {
                // Give up on the unfinished record (its lines stay readable in the raw view) and look again here.
                for (var skipped = open; skipped < number; skipped++)
                {
                    Unread(skipped, ref unread, ref firstUnread);
                }

                open = 0;
                if (startsNew)
                {
                    if (line.Contains(EndMarker, StringComparison.Ordinal))
                    {
                        Add(first, last, severity, number, number, line);
                    }
                    else
                    {
                        open = number;
                    }
                }
                else
                {
                    Unread(number, ref unread, ref firstUnread);
                }
            }
        }

        if (open != 0)
        {
            for (var skipped = open; skipped <= number; skipped++)
            {
                Unread(skipped, ref unread, ref firstUnread);
            }
        }

        return new CmTraceTable(lines, first.ToArray(), last.ToArray(), severity.ToArray(), unread, firstUnread);
    }

    private static void Unread(int line, ref int count, ref int firstLine)
    {
        count++;
        if (firstLine == 0)
        {
            firstLine = line;
        }
    }

    private static void Add(List<int> first, List<int> last, List<byte> severity, int from, int to, string finalLine)
    {
        first.Add(from);
        last.Add(to);
        var attributes = Attributes(finalLine.AsSpan(finalLine.LastIndexOf(EndMarker, StringComparison.Ordinal) + EndMarker.Length));
        severity.Add((byte)SeverityOfType(attributes.GetValueOrDefault("type")));
    }

    private static LogSeverity SeverityOfType(string? type) => type switch
    {
        "1" => LogSeverity.Information,
        "2" => LogSeverity.Warning,
        "3" => LogSeverity.Error,
        _ => LogSeverity.None,
    };

    public LogSeverity SeverityOf(int row) => (LogSeverity)_severity[row];

    public int RowOfLine(int line)
    {
        var index = Array.BinarySearch(_first, line);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 && line <= _last[index] ? index : -1;
    }

    public TableRowData GetRow(int row) => GetRows(row, 1)[0];

    /// <summary>Reads records <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> − 1 with one read of the file.</summary>
    public IReadOnlyList<TableRowData> GetRows(int start, int count)
    {
        count = Math.Min(count, RowCount - start);
        if (start < 0 || count <= 0)
        {
            return Array.Empty<TableRowData>();
        }

        var from = _first[start];
        var to = _last[start + count - 1];
        var block = _lines.ReadLines(from - 1, to - from + 1);

        var rows = new TableRowData[count];
        for (var i = 0; i < count; i++)
        {
            var row = start + i;
            var lineCount = _last[row] - _first[row] + 1;
            var offset = _first[row] - from;
            var text = lineCount == 1
                ? block[offset]
                : string.Join('\n', block.Skip(offset).Take(lineCount));
            rows[i] = Parse(text, _first[row], lineCount);
        }

        return rows;
    }

    private static TableRowData Parse(string text, int firstLine, int lineCount)
    {
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = text.LastIndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            return new TableRowData(new[] { string.Empty, string.Empty, string.Empty, string.Empty, text }, firstLine, lineCount, text);
        }

        var message = text.Substring(start + StartMarker.Length, end - start - StartMarker.Length);
        var attributes = Attributes(text.AsSpan(end + EndMarker.Length));

        attributes.TryGetValue("component", out var component);
        attributes.TryGetValue("thread", out var thread);
        attributes.TryGetValue("type", out var type);
        attributes.TryGetValue("context", out var context);
        attributes.TryGetValue("file", out var file);

        var time = TimeText(attributes.GetValueOrDefault("date"), attributes.GetValueOrDefault("time"));
        var level = SeverityOfType(type) switch
        {
            LogSeverity.Information => "Information",
            LogSeverity.Warning => "Warning",
            LogSeverity.Error => "Error",
            _ => string.Empty,
        };

        var oneLine = message.Replace("\r\n", " ↵ ", StringComparison.Ordinal)
            .Replace("\n", " ↵ ", StringComparison.Ordinal)
            .Replace("\r", " ↵ ", StringComparison.Ordinal);

        var detail = new StringBuilder();
        detail.Append("Time: ").Append(time).Append("   Level: ").Append(level.Length == 0 ? "-" : level)
            .Append("   Component: ").Append(component).Append("   Thread: ").Append(thread).AppendLine();
        if (!string.IsNullOrEmpty(context) || !string.IsNullOrEmpty(file))
        {
            detail.Append("Context: ").Append(context).Append("   Source: ").Append(file).AppendLine();
        }

        detail.AppendLine().Append(message);

        return new TableRowData(new[] { time, level, component ?? string.Empty, thread ?? string.Empty, oneLine }, firstLine, lineCount, detail.ToString());
    }

    /// <summary>As written in the log: date then time, with the zone suffix left off (the log does not say which zone).</summary>
    private static string TimeText(string? date, string? time)
    {
        if (time is null)
        {
            return date ?? string.Empty;
        }

        // The time carries a zone bias after the seconds ("14:12:00.123+000"); it is not a zone name, so it is dropped.
        var clock = TimeBias().Replace(time, string.Empty);
        if (date is not null
            && DateTime.TryParseExact(
                date.Replace('/', '-') + " " + clock,
                new[] { "M-d-yyyy HH:mm:ss.FFFFFFF", "M-d-yyyy HH:mm:ss", "M-d-yyyy H:mm:ss.FFFFFFF", "M-d-yyyy H:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        return (date is null ? string.Empty : date + " ") + clock;
    }

    private static Dictionary<string, string> Attributes(ReadOnlySpan<char> tail)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in AttributePattern().EnumerateMatches(tail))
        {
            var pair = tail.Slice(match.Index, match.Length).ToString();
            var equals = pair.IndexOf('=');
            map[pair[..equals]] = pair[(equals + 2)..^1];
        }

        return map;
    }

    [GeneratedRegex(@"[A-Za-z_]+=""[^""]*""")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"[+-]\d+$")]
    private static partial Regex TimeBias();
}
