using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiagnosticStudio.App.ViewModels.TextViewer;

/// <summary>Free-hand text selection: mark any run of text with the mouse and copy it, as in a text editor.</summary>
public sealed partial class TextViewerViewModel
{
    private const int ReadChunk = 2_000;
    private const int CountCharactersUpToLines = 5_000;

    /// <summary>Select whole lines (the list's own selection) instead of text; the default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeSelection))]
    private bool _lineSelection = true;

    /// <summary>Marking text with the mouse, when whole-line selection is switched off.</summary>
    public bool FreeSelection => !LineSelection;

    /// <summary>The marked text, or <see cref="LogSelection.None"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextSelection), nameof(SelectionInfo))]
    private LogSelection _selection;

    public bool HasTextSelection => !Selection.IsEmpty;

    /// <summary>"3 lines, 142 characters selected" while text is marked; empty otherwise.</summary>
    public string SelectionInfo
    {
        get
        {
            if (Selection.IsEmpty)
            {
                return string.Empty;
            }

            var culture = CultureInfo.CurrentCulture;
            var lines = Selection.LineCount;
            var lineText = lines == 1 ? "1 line" : string.Create(culture, $"{lines:N0} lines");
            if (lines > CountCharactersUpToLines)
            {
                return lineText + " selected";
            }

            var characters = SelectedText(out _).Length;
            return string.Create(culture, $"{lineText}, {characters:N0} characters selected");
        }
    }

    partial void OnLineSelectionChanged(bool value)
    {
        // The two kinds of selection do not mix: switching clears the one being left.
        if (value)
        {
            Selection = LogSelection.None;
        }
    }

    /// <summary>Marks from <paramref name="anchor"/> to <paramref name="caret"/>; positions outside the file are brought inside it.</summary>
    public void Select(TextPosition anchor, TextPosition caret) =>
        Selection = new LogSelection(Clamp(anchor), Clamp(caret));

    public void ClearTextSelection() => Selection = LogSelection.None;

    /// <summary>Marks the whole file.</summary>
    public void SelectAll()
    {
        if (Source.LineCount == 0)
        {
            return;
        }

        Selection = new LogSelection(new TextPosition(1, 0), new TextPosition(Source.LineCount, LineLength(Source.LineCount)));
    }

    /// <summary>Marks one whole line (a click on its number, or a triple click).</summary>
    public void SelectLineAt(int line)
    {
        if (line < 1 || line > Source.LineCount)
        {
            return;
        }

        Selection = new LogSelection(new TextPosition(line, 0), new TextPosition(line, LineLength(line)));
    }

    /// <summary>
    /// Marks the word at a place (a double click). A word runs between spaces and the characters that usually delimit
    /// values in logs (quotes, brackets, commas, '=', '|'), so a path, a GUID or a time stamp is selected whole.
    /// </summary>
    public void SelectWordAt(int line, int column)
    {
        if (line < 1 || line > Source.LineCount)
        {
            return;
        }

        var text = LineText(line);
        if (text.Length == 0)
        {
            return;
        }

        var at = Math.Clamp(column, 0, text.Length - 1);
        var kind = IsWordCharacter(text[at]);
        var start = at;
        while (start > 0 && IsWordCharacter(text[start - 1]) == kind)
        {
            start--;
        }

        var end = at + 1;
        while (end < text.Length && IsWordCharacter(text[end]) == kind)
        {
            end++;
        }

        Selection = new LogSelection(new TextPosition(line, start), new TextPosition(line, end));
    }

    /// <summary>
    /// The marked text, lines joined with line breaks. Capped at <see cref="MaxCopyLines"/> lines so that a selection
    /// of a huge file cannot exhaust memory; <paramref name="truncated"/> says when it was.
    /// </summary>
    public string SelectedText(out bool truncated)
    {
        truncated = false;
        if (Selection.IsEmpty)
        {
            return string.Empty;
        }

        var start = Selection.Start;
        var end = Selection.End;
        var last = end.Line;
        if (last - start.Line + 1 > MaxCopyLines)
        {
            last = start.Line + MaxCopyLines - 1;
            end = new TextPosition(last, int.MaxValue);
            truncated = true;
        }

        var text = new StringBuilder();
        for (var from = start.Line; from <= last; from += ReadChunk)
        {
            var batch = Source.ReadLines(from - 1, Math.Min(ReadChunk, last - from + 1));
            for (var i = 0; i < batch.Count; i++)
            {
                var number = from + i;
                var line = batch[i];
                var s = number == start.Line ? Math.Clamp(start.Column, 0, line.Length) : 0;
                var e = number == end.Line ? Math.Clamp(end.Column, 0, line.Length) : line.Length;
                if (number > start.Line)
                {
                    text.Append(Environment.NewLine);
                }

                text.Append(line, s, Math.Max(0, e - s));
            }
        }

        return text.ToString();
    }

    public int LineLength(int line) => LineText(line).Length;

    private string LineText(int line) =>
        Source.ReadLines(line - 1, 1) is { Count: > 0 } lines ? lines[0] : string.Empty;

    private TextPosition Clamp(TextPosition position) =>
        new(Math.Clamp(position.Line, 1, Math.Max(1, Source.LineCount)), Math.Max(0, position.Column));

    private static bool IsWordCharacter(char c) => !char.IsWhiteSpace(c) && "\"'()[]{}<>,;|=".IndexOf(c) < 0;
}
