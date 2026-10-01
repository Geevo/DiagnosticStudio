using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DiagnosticStudio.App.Views;

/// <summary>A TextBlock that shows a preview with one range (the search match) emphasised.</summary>
public sealed class MatchTextBlock : TextBlock
{
    private static readonly Brush MatchBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A)));
    private static readonly Brush MatchForeground = Freeze(new SolidColorBrush(Colors.Black));

    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(string), typeof(MatchTextBlock),
        new PropertyMetadata(string.Empty, (d, _) => ((MatchTextBlock)d).Rebuild()));

    public static readonly DependencyProperty MatchStartProperty = DependencyProperty.Register(
        nameof(MatchStart), typeof(int), typeof(MatchTextBlock),
        new PropertyMetadata(0, (d, _) => ((MatchTextBlock)d).Rebuild()));

    public static readonly DependencyProperty MatchLengthProperty = DependencyProperty.Register(
        nameof(MatchLength), typeof(int), typeof(MatchTextBlock),
        new PropertyMetadata(0, (d, _) => ((MatchTextBlock)d).Rebuild()));

    public string? Preview
    {
        get => (string?)GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    public int MatchStart
    {
        get => (int)GetValue(MatchStartProperty);
        set => SetValue(MatchStartProperty, value);
    }

    public int MatchLength
    {
        get => (int)GetValue(MatchLengthProperty);
        set => SetValue(MatchLengthProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var text = Preview ?? string.Empty;
        var start = MatchStart;
        var length = MatchLength;

        if (length <= 0 || start < 0 || start + length > text.Length)
        {
            Inlines.Add(new Run(text));
            return;
        }

        if (start > 0)
        {
            Inlines.Add(new Run(text[..start]));
        }

        Inlines.Add(new Run(text.Substring(start, length))
        {
            Background = MatchBackground,
            Foreground = MatchForeground,
            FontWeight = FontWeights.SemiBold,
        });

        if (start + length < text.Length)
        {
            Inlines.Add(new Run(text[(start + length)..]));
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
