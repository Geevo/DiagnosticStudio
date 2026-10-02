namespace DiagnosticStudio.Core.Documents;

public enum TableFormat
{
    /// <summary>A Configuration Manager / Intune log: records written as <c>&lt;![LOG[message]LOG]!&gt;&lt;time=... component=... type=... thread=...&gt;</c>.</summary>
    CmTrace,

    /// <summary>Delimited text (comma, semicolon, tab or pipe) with an optional header row.</summary>
    Csv,
}

public enum TableColumnKind
{
    Text,
    Time,

    /// <summary>Information, Warning or Error; coloured in the viewer.</summary>
    Level,
    Number,
}

/// <param name="Width">A starting width in device-independent pixels; the user can resize it.</param>
public sealed record TableColumn(string Name, TableColumnKind Kind, double Width);

/// <summary>One record of a table, as read from the raw text.</summary>
/// <param name="Cells">One value per column. A multi-line value is shown on one line with a return mark.</param>
/// <param name="FirstLine">One-based first line of the record in the raw text.</param>
/// <param name="LineCount">Lines the record spans (more than one when a value holds line breaks).</param>
/// <param name="Detail">The whole record as text, for the detail pane.</param>
public readonly record struct TableRowData(IReadOnlyList<string> Cells, int FirstLine, int LineCount, string Detail)
{
    public int LastLine => FirstLine + Math.Max(1, LineCount) - 1;
}

/// <summary>
/// Records of a log or a delimited file, read on demand from the raw text. Only where each record starts is kept in
/// memory, so a file with millions of records costs a few bytes each.
/// </summary>
public interface ITableSource
{
    IReadOnlyList<TableColumn> Columns { get; }

    int RowCount { get; }

    /// <summary>Raw lines before the first record (a header row), which no record covers.</summary>
    int HeaderLines { get; }

    /// <summary>Whether records have a level, so the viewer offers a level filter.</summary>
    bool HasLevels { get; }

    /// <summary>Reads record <paramref name="row"/> (zero-based) from the raw text.</summary>
    TableRowData GetRow(int row);

    /// <summary>
    /// Reads <paramref name="count"/> consecutive records starting at <paramref name="start"/> (fewer at the end of the
    /// file) with one read of the raw text; how a page of rows is filled.
    /// </summary>
    IReadOnlyList<TableRowData> GetRows(int start, int count);

    /// <summary>The record that contains raw line <paramref name="line"/> (one-based), or <c>-1</c> for a header or stray line.</summary>
    int RowOfLine(int line);

    LogSeverity SeverityOf(int row);
}

/// <summary>A log or delimited file shown as a table next to its raw text.</summary>
public sealed record TableDocument : DiagnosticDocument
{
    public required TableFormat Format { get; init; }

    public required ITableSource Table { get; init; }

    /// <summary>The complete file as text; always available.</summary>
    public required ITextLineSource RawSource { get; init; }

    /// <summary>Raw lines that belong to no record (text between records, lines that did not fit the format).</summary>
    public int UnreadLines { get; init; }

    /// <summary>The first such line, one-based, or 0.</summary>
    public int FirstUnreadLine { get; init; }

    /// <summary>What was assumed about the file, for the status line (for example the delimiter that was detected).</summary>
    public string? Note { get; init; }
}

/// <summary>Finds the raw text of whichever kind of document has one, so rules, search and the timeline need not know every kind.</summary>
public static class DocumentText
{
    /// <summary>The lines of <paramref name="document"/>, or <c>null</c> for documents that are not text (event logs, registry exports).</summary>
    public static ITextLineSource? LinesOf(DiagnosticDocument document) => document switch
    {
        TextDocument text => text.Lines,
        TableDocument table => table.RawSource,
        HtmlDocument html => html.RawSource,
        StructuredDocument structured => structured.RawSource,
        RegistryDocument registry => registry.RawSource,
        _ => null,
    };
}
