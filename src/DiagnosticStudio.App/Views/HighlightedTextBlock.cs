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

    private void Rebuild()
    {
        Inlines.Clear();
        if (Line is not { } line)
        {
            return;
        }

        var text = line.Text;
        var spans = new List<(int Start, int Length, bool IsMatch)>();

        var query = Highlight;
        if (!string.IsNullOrEmpty(query))
        {
            var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var from = 0;
            int at;
            while (spans.Count < MaxHighlightsPerLine && (at = text.IndexOf(query, from, comparison)) >= 0)
            {
                spans.Add((at, query.Length, true));
                from = at + query.Length;
            }
        }

        if (line.Info.HasTimestampSpan
            && line.Info.TimestampStart + line.Info.TimestampLength <= text.Length
            && !spans.Any(s => s.Start < line.Info.TimestampStart + line.Info.TimestampLength
                               && line.Info.TimestampStart < s.Start + s.Length))
        {
            spans.Add((line.Info.TimestampStart, line.Info.TimestampLength, false));
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        if (spans.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var position = 0;
        foreach (var (start, length, isMatch) in spans)
        {
            if (start > position)
            {
                Inlines.Add(new Run(text[position..start]));
            }

            var run = new Run(text.Substring(start, length));
            if (isMatch)
            {
                run.Background = MatchBackground;
                run.Foreground = MatchForeground;
            }
            else
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "TimestampTextBrush");
            }

            Inlines.Add(run);
            position = start + length;
        }

        if (position < text.Length)
        {
            Inlines.Add(new Run(text[position..]));
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
