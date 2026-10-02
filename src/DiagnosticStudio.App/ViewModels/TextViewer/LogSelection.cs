namespace DiagnosticStudio.App.ViewModels.TextViewer;

/// <summary>A place in the text: a one-based line and a zero-based character in it.</summary>
public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;

    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;

    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Text the engineer has marked with the mouse, from where the press happened (<see cref="Anchor"/>) to where the
/// pointer is now (<see cref="Caret"/>), which may be earlier in the file than the anchor. Nothing is selected when
/// the two are equal.
/// </summary>
public readonly record struct LogSelection(TextPosition Anchor, TextPosition Caret)
{
    public static LogSelection None => default;

    public bool IsEmpty => Anchor == Caret;

    public TextPosition Start => Anchor <= Caret ? Anchor : Caret;

    public TextPosition End => Anchor <= Caret ? Caret : Anchor;

    /// <summary>Number of lines the selection touches (0 when empty).</summary>
    public int LineCount => IsEmpty ? 0 : End.Line - Start.Line + 1;

    /// <summary>
    /// The part of line <paramref name="line"/> (of <paramref name="lineLength"/> characters) that is selected, or
    /// <c>null</c> when none of it is. A line in the middle of a selection is selected whole.
    /// </summary>
    public (int Start, int Length)? SpanOnLine(int line, int lineLength)
    {
        if (IsEmpty || line < Start.Line || line > End.Line)
        {
            return null;
        }

        var from = line == Start.Line ? Start.Column : 0;
        var to = line == End.Line ? End.Column : lineLength;
        from = Math.Clamp(from, 0, lineLength);
        to = Math.Clamp(to, 0, lineLength);
        return to > from ? (from, to - from) : null;
    }
}
