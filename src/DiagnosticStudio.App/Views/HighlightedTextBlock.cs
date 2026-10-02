using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DiagnosticStudio.App.ViewModels.TextViewer;

namespace DiagnosticStudio.App.Views;

/// <summary>
/// TextBlock for a log line that dims the recognised timestamp and highlights occurrences of the find
/// text. Display only; the underlying line text is never altered.
/// </summary>
public sealed class HighlightedTextBlock : TextBlock
{
    private const int MaxHighlightsPerLine = 100;

    private static readonly Brush MatchBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A)));
    private static readonly Brush MatchForeground = Freeze(new SolidColorBrush(Colors.Black));

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(LineViewModel), typeof(HighlightedTextBlock),
        new PropertyMetadata(null, (d, _) => ((HighlightedTextBlock)d).Rebuild()));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(string), typeof(HighlightedTextBlock),
        new PropertyMetadata(string.Empty, (d, _) => ((HighlightedTextBlock)d).Rebuild()));

    public static readonly DependencyProperty MatchCaseProperty = DependencyProperty.Register(
        nameof(MatchCase), typeof(bool), typeof(HighlightedTextBlock),
        new PropertyMetadata(false, (d, _) => ((HighlightedTextBlock)d).Rebuild()));

    public LineViewModel? Line
    {
        get => (LineViewModel?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public string? Highlight
    {
        get => (string?)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public bool MatchCase
    {
        get => (bool)GetValue(MatchCaseProperty);
        set => SetValue(MatchCaseProperty, value);
    }

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(LogSelection), typeof(HighlightedTextBlock),
        new PropertyMetadata(LogSelection.None, (d, e) => ((HighlightedTextBlock)d).OnSelectionChanged((LogSelection)e.OldValue, (LogSelection)e.NewValue)));

    /// <summary>The text the engineer has marked in the viewer; the part that falls on this line is drawn marked.</summary>
    public LogSelection Selection
    {
        get => (LogSelection)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    // Marking text changes the selection many times a second; only the lines it touches before or after need redrawing.
    private void OnSelectionChanged(LogSelection before, LogSelection after)
    {
        if (Line is not { } line)
        {
            return;
        }

        var length = line.Text.Length;
        if (before.SpanOnLine(line.LineNumber, length) is not null || after.SpanOnLine(line.LineNumber, length) is not null)
        {
            Rebuild();
        }
    }

    /// <summary>The character of the line under <paramref name="point"/> (relative to this element): where a click lands between letters.</summary>
    public int ColumnAt(Point point)
    {
        var length = Line?.Text.Length ?? 0;
        if (GetPositionFromPoint(point, snapToText: true) is not { } pointer)
        {
            return length;
        }

        var column = 0;
        foreach (var inline in Inlines)
        {
            if (inline is not Run run)
            {
                continue;
            }

            // Before the first character, or in the gap between two pieces: the next character is the one at 'column'.
            if (pointer.CompareTo(run.ContentStart) < 0)
            {
                return Math.Clamp(column, 0, length);
            }

            if (pointer.CompareTo(run.ContentEnd) <= 0)
            {
                return Math.Clamp(column + run.ContentStart.GetOffsetToPosition(pointer), 0, length);
            }

            column += run.Text.Length;
        }

        return length;
    }

    private void Rebuild()
    {
        Inlines.Clear();
        if (Line is not { } line)
        {
            return;
        }

        var text = line.Text;
        var matches = new List<(int Start, int Length)>();

        var query = Highlight;
        if (!string.IsNullOrEmpty(query))
        {
            var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var from = 0;
            int at;
            while (matches.Count < MaxHighlightsPerLine && (at = text.IndexOf(query, from, comparison)) >= 0)
            {
                matches.Add((at, query.Length));
                from = at + query.Length;
            }
        }

        (int Start, int Length)? timestamp = null;
        if (line.Info.HasTimestampSpan
            && line.Info.TimestampStart + line.Info.TimestampLength <= text.Length
            && !matches.Any(s => s.Start < line.Info.TimestampStart + line.Info.TimestampLength
                                 && line.Info.TimestampStart < s.Start + s.Length))
        {
            timestamp = (line.Info.TimestampStart, line.Info.TimestampLength);
        }

        var selection = Selection.SpanOnLine(line.LineNumber, text.Length);
        if (matches.Count == 0 && timestamp is null && selection is null)
        {
            Inlines.Add(new Run(text));
            return;
        }

        // Cut the line wherever a highlight starts or ends, then style each piece by what covers it.
        var cuts = new SortedSet<int> { 0, text.Length };
        foreach (var (start, length) in matches)
        {
            cuts.Add(start);
            cuts.Add(Math.Min(text.Length, start + length));
        }

        if (timestamp is { } ts)
        {
            cuts.Add(ts.Start);
            cuts.Add(ts.Start + ts.Length);
        }

        if (selection is { } sel)
        {
            cuts.Add(sel.Start);
            cuts.Add(sel.Start + sel.Length);
        }

        var points = cuts.ToArray();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var from = points[i];
            var to = points[i + 1];
            var run = new Run(text[from..to]);

            var isMatch = matches.Any(m => from >= m.Start && to <= m.Start + m.Length);
            var isTimestamp = timestamp is { } t && from >= t.Start && to <= t.Start + t.Length;
            var isSelected = selection is { } s && from >= s.Start && to <= s.Start + s.Length;

            if (isMatch)
            {
                run.Background = MatchBackground;
                run.Foreground = MatchForeground;
            }
            else if (isTimestamp)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "TimestampTextBrush");
            }

            if (isSelected)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, "TextSelectionBrush");
            }

            Inlines.Add(run);
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
