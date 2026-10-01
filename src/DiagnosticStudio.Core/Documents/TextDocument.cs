namespace DiagnosticStudio.Core.Documents;

/// <summary>
/// Random access to the lines of a text artifact without holding the whole file in memory.
/// Line numbers passed to this interface are zero-based; <see cref="DiagnosticStudio.Core.Navigation.DiagnosticLocation"/>
/// line numbers are one-based.
/// </summary>
public interface ITextLineSource
{
    int LineCount { get; }

    long ByteLength { get; }

    string EncodingName { get; }

    /// <summary>Reads up to <paramref name="count"/> lines starting at <paramref name="startLine"/>. Fewer are returned at end of file.</summary>
    IReadOnlyList<string> ReadLines(int startLine, int count);

    /// <summary>Streams lines sequentially from <paramref name="startLine"/>. Intended for background scans.</summary>
    IEnumerable<string> EnumerateLines(int startLine = 0);
}

/// <summary>A text artifact (log, command output, or raw source of a structured format).</summary>
public sealed record TextDocument : DiagnosticDocument
{
    public required ITextLineSource Lines { get; init; }
}

public enum LogSeverity : byte
{
    None = 0,
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>What was recognised on a single log line. Spans are character offsets into the line.</summary>
/// <param name="Timestamp">Parsed value when the format is unambiguous; as written in the log (no time zone applied).</param>
/// <param name="TimestampStart">Start of the recognised timestamp text, or -1 when none.</param>
public readonly record struct LogLineInfo(
    LogSeverity Severity,
    DateTime? Timestamp,
    int TimestampStart,
    int TimestampLength)
{
    public static LogLineInfo None { get; } = new(LogSeverity.None, null, -1, 0);

    public bool HasTimestampSpan => TimestampStart >= 0 && TimestampLength > 0;
}
