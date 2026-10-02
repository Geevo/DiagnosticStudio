using System.Globalization;
using System.Text;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers.Tables;

/// <summary>
/// Reads delimited text (comma, semicolon, tab or pipe) following RFC 4180: a value in quotes may hold the delimiter,
/// line breaks and doubled quotes. The delimiter is detected from the first lines; a first row of names is taken as
/// the header. Where each record starts and ends is found in one pass; the values are read from the text on demand.
/// </summary>
public sealed class CsvTable : ITableSource
{
    public const int MaxLinesPerRecord = 20_000;
    private const int SampleRecords = 1_000;
    private const int DelimiterSampleLines = 100;

    private static readonly char[] Candidates = { ',', '\t', ';', '|' };

    private readonly ITextLineSource _lines;
    private readonly int[] _first;
    private readonly int[] _last;

    private CsvTable(
        ITextLineSource lines,
        char delimiter,
        int[] first,
        int[] last,
        int headerLines,
        TableColumn[] columns,
        bool hasHeader,
        int unreadLines,
        int firstUnread)
    {
        _lines = lines;
        Delimiter = delimiter;
        _first = first;
        _last = last;
        HeaderLines = headerLines;
        Columns = columns;
        HasHeader = hasHeader;
        UnreadLines = unreadLines;
        FirstUnreadLine = firstUnread;
    }

    public char Delimiter { get; }

    public bool HasHeader { get; }

    public IReadOnlyList<TableColumn> Columns { get; }

    public int RowCount => _first.Length;

    public int HeaderLines { get; }

    public bool HasLevels => false;

    /// <summary>Lines that could not be placed in a record (a quoted value that never closed).</summary>
    public int UnreadLines { get; }

    public int FirstUnreadLine { get; }

    public string DelimiterName => Delimiter switch
    {
        '\t' => "tab",
        ',' => "comma",
        ';' => "semicolon",
        '|' => "pipe",
        _ => "'" + Delimiter + "'",
    };

    public static CsvTable Build(ITextLineSource lines, CancellationToken cancellationToken = default)
    {
        var delimiter = DetectDelimiter(lines);
        var starts = new List<int>();
        var ends = new List<int>();
        var unread = 0;
        var firstUnread = 0;

        // A quote that never closes would swallow the rest of the file. After MaxLinesPerRecord lines the first line is
        // taken as a record on its own and reading starts again on the line after it.
        var resume = 0; // zero-based line to start reading from
        while (true)
        {
            var number = resume;
            var openFrom = 0;
            var inQuotes = false;
            var restartAt = -1;

            foreach (var line in lines.EnumerateLines(resume))
            {
                number++;
                if ((number & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (openFrom == 0)
                {
                    if (IsBlank(line))
                    {
                        continue; // a blank line between records is not a record
                    }

                    openFrom = number;
                    inQuotes = false;
                }

                if (CountQuotes(line) % 2 == 1)
                {
                    inQuotes = !inQuotes;
                }

                if (!inQuotes)
                {
                    starts.Add(openFrom);
                    ends.Add(number);
                    openFrom = 0;
                }
                else if (number - openFrom >= MaxLinesPerRecord)
                {
                    starts.Add(openFrom);
                    ends.Add(openFrom);
                    unread++;
                    firstUnread = firstUnread == 0 ? openFrom : firstUnread;
                    restartAt = openFrom; // the line after it, as a zero-based index
                    break;
                }
            }

            if (restartAt >= 0)
            {
                resume = restartAt;
                continue;
            }

            if (openFrom != 0)
            {
                // End of the file inside a quoted value: the rest is one record, and the file says so.
                starts.Add(openFrom);
                ends.Add(number);
                unread++;
                firstUnread = firstUnread == 0 ? openFrom : firstUnread;
            }

            break;
        }

        return Finish(lines, delimiter, starts, ends, unread, firstUnread);
    }

    private static CsvTable Finish(ITextLineSource lines, char delimiter, List<int> starts, List<int> ends, int unread, int firstUnread)
    {
        var first = starts.ToArray();
        var last = ends.ToArray();
        if (first.Length == 0)
        {
            return new CsvTable(lines, delimiter, first, last, 0, new[] { new TableColumn("Column 1", TableColumnKind.Text, 200) }, false, unread, firstUnread);
        }

        var headerFields = SplitRecord(lines, first[0], last[0], delimiter);
        var sample = Math.Min(first.Length, SampleRecords);

        var hasHeader = LooksLikeHeader(headerFields) && (first.Length == 1 || SecondRowDiffers(lines, first, last, delimiter, headerFields));
        var dataStart = hasHeader ? 1 : 0;

        var rows = new List<List<string>>();
        var width = headerFields.Count;
        for (var i = dataStart; i < sample; i++)
        {
            var fields = SplitRecord(lines, first[i], last[i], delimiter);
            rows.Add(fields);
            width = Math.Max(width, fields.Count);
        }

        var columns = new TableColumn[width];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < width; c++)
        {
            var name = hasHeader && c < headerFields.Count && !string.IsNullOrWhiteSpace(headerFields[c])
                ? headerFields[c].Trim()
                : "Column " + (c + 1).ToString(CultureInfo.InvariantCulture);
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
            {
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            var values = rows.Select(r => c < r.Count ? r[c] : string.Empty).ToList();
            columns[c] = new TableColumn(unique, KindOf(values), WidthOf(unique, values));
        }

        if (hasHeader)
        {
            // The header is not a record.
            first = first[1..];
            last = last[1..];
        }

        // Raw lines before the first record: the header row and any blank lines above the data.
        var headerLines = first.Length > 0 ? first[0] - 1 : lines.LineCount;
        return new CsvTable(lines, delimiter, first, last, headerLines, columns, hasHeader, unread, firstUnread);
    }

    // ---- reading ----

    public LogSeverity SeverityOf(int row) => LogSeverity.None;

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
            var text = lineCount == 1 ? block[offset] : string.Join('\n', block.Skip(offset).Take(lineCount));
            var fields = SplitFields(text, Delimiter);

            var cells = new string[Math.Max(Columns.Count, fields.Count)];
            for (var c = 0; c < cells.Length; c++)
            {
                cells[c] = c < fields.Count ? OneLine(fields[c]) : string.Empty;
            }

            rows[i] = new TableRowData(cells, _first[row], lineCount, DetailOf(fields));
        }

        return rows;
    }

    private string DetailOf(List<string> fields)
    {
        var detail = new StringBuilder();
        for (var c = 0; c < fields.Count; c++)
        {
            var name = c < Columns.Count ? Columns[c].Name : "Column " + (c + 1).ToString(CultureInfo.InvariantCulture);
            detail.Append(name).Append(": ").AppendLine(fields[c]);
        }

        return detail.ToString().TrimEnd();
    }

    private static string OneLine(string value) =>
        value.IndexOfAny(new[] { '\r', '\n' }) < 0
            ? value
            : value.Replace("\r\n", " ↵ ", StringComparison.Ordinal).Replace("\n", " ↵ ", StringComparison.Ordinal).Replace("\r", " ↵ ", StringComparison.Ordinal);

    // ---- delimiter, header, types ----

    private static char DetectDelimiter(ITextLineSource lines)
    {
        var sample = lines.EnumerateLines().Take(DelimiterSampleLines).Where(l => !IsBlank(l)).ToList();
        if (sample.Count == 0)
        {
            return ',';
        }

        var best = ',';
        var bestScore = 0.0;
        foreach (var candidate in Candidates)
        {
            var counts = sample.Select(l => CountOutsideQuotes(l, candidate)).ToList();
            var modal = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (modal.Key == 0)
            {
                continue;
            }

            // Consistent counts over many lines beat a high count that varies.
            var score = (double)modal.Count() / counts.Count * (1 + Math.Log(modal.Key + 1));
            if (score > bestScore + 1e-9)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }

    // Names, not values: at least half the cells are filled and none of them is a number or a date. (Repeated or blank
    // names are common in real exports and do not stop it being a header.)
    private static bool LooksLikeHeader(List<string> fields)
    {
        var filled = fields.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        return filled.Count >= Math.Max(1, fields.Count / 2)
               && filled.All(f => !double.TryParse(f, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
                                  && !DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    // A header is followed by data. If the second row is just as name-like, the first row is probably data too.
    private static bool SecondRowDiffers(ITextLineSource lines, int[] first, int[] last, char delimiter, List<string> header)
    {
        var second = SplitRecord(lines, first[1], last[1], delimiter);
        return second.Any(f => double.TryParse(f, NumberStyles.Any, CultureInfo.InvariantCulture, out _) || DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
               || second.Count != header.Count
               || !second.SequenceEqual(header, StringComparer.OrdinalIgnoreCase);
    }

    private static TableColumnKind KindOf(List<string> values)
    {
        var filled = values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(200).ToList();
        if (filled.Count == 0)
        {
            return TableColumnKind.Text;
        }

        var numbers = filled.Count(v => double.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _));
        if (numbers >= filled.Count * 0.95)
        {
            return TableColumnKind.Number;
        }

        var times = filled.Count(v => v.Length >= 8 && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        return times >= filled.Count * 0.95 ? TableColumnKind.Time : TableColumnKind.Text;
    }

    private static double WidthOf(string name, List<string> values)
    {
        var longest = Math.Max(name.Length, values.Take(200).Select(v => v.Length).DefaultIfEmpty(0).Max());
        return Math.Clamp((longest * 7.2) + 24, 70, 420);
    }

    // ---- fields ----

    private static List<string> SplitRecord(ITextLineSource lines, int first, int last, char delimiter)
    {
        var block = lines.ReadLines(first - 1, last - first + 1);
        return SplitFields(block.Count == 1 ? block[0] : string.Join('\n', block), delimiter);
    }

    /// <summary>Splits one record into values: quotes group a value, a doubled quote is a quote, and quoted values may hold line breaks.</summary>
    public static List<string> SplitFields(string text, char delimiter)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    value.Append(c);
                }
            }
            else if (c == '"' && value.Length == 0 && !wasQuoted)
            {
                inQuotes = true;
                wasQuoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(value.ToString());
                value.Clear();
                wasQuoted = false;
            }
            else
            {
                value.Append(c);
            }
        }

        fields.Add(value.ToString());
        return fields;
    }

    private static int CountQuotes(string line)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == '"')
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsBlank(string line)
    {
        foreach (var c in line)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }
}
