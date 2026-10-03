using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.App.ViewModels.TextViewer;

/// <summary>Line-numbered viewer with in-file find, go-to-line, word wrap and copy over an <see cref="ITextLineSource"/>.</summary>
public sealed partial class TextViewerViewModel : ObservableObject, ILocationNavigable, ISearchHighlightable, ICurrentPosition
{
    public const int MaxCopyLines = 100_000;
    private const int FindDebounceMilliseconds = 250;

    private const int FilterDebounceMilliseconds = 250;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _filterCts;
    private int[] _matches = Array.Empty<int>();
    private int[] _stepMatches = Array.Empty<int>();
    private bool _matchesTruncated;
    private bool _quiet;

    public TextViewerViewModel(ITextLineSource source)
    {
        Source = source;
        _lines = new VirtualLineList(source);
        GutterWidth = (Math.Max(1, source.LineCount.ToString(CultureInfo.InvariantCulture).Length) * 8.0) + 16;
        InfoText = string.Create(
            CultureInfo.CurrentCulture,
            $"{source.EncodingName} · {source.LineCount:N0} lines · {source.ByteLength:N0} bytes");
    }

    public ITextLineSource Source { get; }
    public double GutterWidth { get; }
    public string InfoText { get; }

    /// <summary>Raised when the view should scroll a line (one-based) into view and select it.</summary>
    public event EventHandler<int>? ScrollToLineRequested;

    /// <summary>Raised when the user deliberately jumped to a line (go-to-line); feeds navigation history.</summary>
    public event EventHandler<int>? LineNavigated;

    /// <summary>The lines being shown: all of them, or only those the filter lets through.</summary>
    [ObservableProperty]
    private VirtualLineList _lines;

    /// <summary>Show only the lines that contain this text; the lines keep their numbers.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _filterStatus = string.Empty;

    [ObservableProperty]
    private bool _isFiltering;

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _wrapText;

    [ObservableProperty]
    private string _goToLineText = string.Empty;

    /// <summary>One-based selected/current line; 0 when nothing is selected.</summary>
    [ObservableProperty]
    private int _currentLine;

    /// <summary>Query the match highlighting currently reflects.</summary>
    [ObservableProperty]
    private string _highlightText = string.Empty;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    [ObservableProperty]
    private string? _goToLineError;

    /// <summary>The in-flight or most recent search; lets callers and tests await completion.</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public DiagnosticLocation? CurrentPosition(Guid artifactId) =>
        CurrentLine > 0 ? DiagnosticLocation.ForLine(artifactId, CurrentLine) : null;

    public int MatchCount => _matches.Length;

    /// <summary>The filter being applied; lets callers and tests await it.</summary>
    public Task PendingFilter { get; private set; } = Task.CompletedTask;

    // ---- filter ----

    partial void OnFilterTextChanged(string value) => RestartFilter();

    private void RestartFilter()
    {
        if (_quiet)
        {
            return;
        }

        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        PendingFilter = RunFilterAsync(FilterText.Trim(), _filterCts.Token);
    }

    private async Task RunFilterAsync(string text, CancellationToken token)
    {
        try
        {
            if (text.Length == 0)
            {
                IsFiltering = false;
                ShowLines(null);
                return;
            }

            IsFiltering = true;
            await Task.Delay(FilterDebounceMilliseconds, token).ConfigureAwait(true);

            var result = await TextSearch.FindLinesAsync(Source, text, matchCase: false, token, int.MaxValue).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            ShowLines(result.Lines as int[] ?? result.Lines.ToArray());
            IsFiltering = false;
        }
        catch (OperationCanceledException)
        {
            // A newer filter owns the list now.
        }
    }

    private void ShowLines(int[]? view)
    {
        Lines = new VirtualLineList(Source, view);
        Lines.SetMatches(_matches);
        _stepMatches = StepMatches();
        FilterStatus = view is null
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"{view.Length:N0} of {Source.LineCount:N0} lines");
        UpdateSearchStatus();

        // The list was rebuilt: put the selection back on the line the user was on, when that line is still shown.
        if (CurrentLine > 0 && Lines.IsShown(CurrentLine))
        {
            ScrollToLineRequested?.Invoke(this, CurrentLine);
        }
    }

    /// <summary>Shows every line again, without waiting; used before going to a line the filter hides.</summary>
    public void ClearFilter()
    {
        _filterCts?.Cancel();
        _quiet = true;
        try
        {
            FilterText = string.Empty;
        }
        finally
        {
            _quiet = false;
        }

        IsFiltering = false;
        PendingFilter = Task.CompletedTask;
        if (Lines.IsFiltered)
        {
            ShowLines(null);
        }
    }

    // ---- find ----

    /// <summary>The found lines that are shown, which F3 steps through.</summary>
    private int[] StepMatches() => Lines.IsFiltered ? _matches.Where(Lines.IsShown).ToArray() : _matches;

    partial void OnFindTextChanged(string value) => RestartSearch(debounce: true);

    partial void OnMatchCaseChanged(bool value) => RestartSearch(debounce: false);

    private void RestartSearch(bool debounce)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        PendingSearch = RunSearchAsync(FindText, MatchCase, debounce, _searchCts.Token);
    }

    private async Task RunSearchAsync(string query, bool matchCase, bool debounce, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrEmpty(query))
            {
                ApplyMatches(Array.Empty<int>(), truncated: false, query);
                SearchStatus = string.Empty;
                return;
            }

            SearchStatus = "Searching...";
            if (debounce)
            {
                await Task.Delay(FindDebounceMilliseconds, token).ConfigureAwait(true);
            }

            var result = await TextSearch.FindLinesAsync(Source, query, matchCase, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            ApplyMatches(result.Lines.ToArray(), result.Truncated, query);

            // Incremental find: land on the first match at or after where the user is.
            if (_stepMatches.Length > 0)
            {
                JumpToMatch(FirstMatchAtOrAfter(Math.Max(CurrentLine, 1)));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query; that run owns the status text.
        }
    }

    private void ApplyMatches(int[] lines, bool truncated, string query)
    {
        _matches = lines;
        _matchesTruncated = truncated;
        HighlightText = query;
        Lines.SetMatches(lines);
        _stepMatches = StepMatches();
        UpdateSearchStatus();
        OnPropertyChanged(nameof(MatchCount));
    }

    [RelayCommand]
    private void FindNext()
    {
        if (_stepMatches.Length == 0)
        {
            return;
        }

        var index = Array.BinarySearch(_stepMatches, CurrentLine);
        var next = index >= 0 ? index + 1 : ~index;
        JumpToMatch(next >= _stepMatches.Length ? 0 : next);
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (_stepMatches.Length == 0)
        {
            return;
        }

        var index = Array.BinarySearch(_stepMatches, CurrentLine);
        var previous = (index >= 0 ? index : ~index) - 1;
        JumpToMatch(previous < 0 ? _stepMatches.Length - 1 : previous);
    }

    private int FirstMatchAtOrAfter(int line)
    {
        var index = Array.BinarySearch(_stepMatches, line);
        var at = index >= 0 ? index : ~index;
        return at >= _stepMatches.Length ? 0 : at;
    }

    private void JumpToMatch(int matchIndex)
    {
        CurrentLine = _stepMatches[matchIndex];
        ScrollToLineRequested?.Invoke(this, CurrentLine);
        UpdateSearchStatus();
    }

    private void UpdateSearchStatus()
    {
        if (string.IsNullOrEmpty(HighlightText))
        {
            SearchStatus = string.Empty;
            return;
        }

        if (_stepMatches.Length == 0)
        {
            SearchStatus = "No matches";
            return;
        }

        var count = _stepMatches.Length.ToString("N0", CultureInfo.CurrentCulture) + (_matchesTruncated ? "+" : string.Empty);
        var position = Array.BinarySearch(_stepMatches, CurrentLine);
        SearchStatus = position >= 0
            ? $"{position + 1:N0} of {count}"
            : $"{count} matching lines";
    }

    /// <summary>Called by the view when the user selects a line, so find continues from there.</summary>
    public void SetCurrentLineFromSelection(int line)
    {
        if (line == CurrentLine)
        {
            return;
        }

        CurrentLine = line;
        UpdateSearchStatus();
    }

    [RelayCommand]
    private void GoToLine()
    {
        GoToLineError = null;
        if (!int.TryParse(GoToLineText.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var line) || line < 1)
        {
            GoToLineError = "Enter a line number.";
            return;
        }

        if (Source.LineCount == 0)
        {
            GoToLineError = "The file is empty.";
            return;
        }

        NavigateToLine(line, userInitiated: true);
    }

    /// <summary>Scrolls to and selects a line (clamped to the file). User-initiated jumps are reported for history.</summary>
    public void NavigateToLine(int line, bool userInitiated = false)
    {
        if (Source.LineCount == 0)
        {
            return;
        }

        var clamped = Math.Clamp(line, 1, Source.LineCount);
        if (!Lines.IsShown(clamped))
        {
            ClearFilter(); // the line is one the filter hides
        }

        CurrentLine = clamped;
        ScrollToLineRequested?.Invoke(this, clamped);
        UpdateSearchStatus();
        if (userInitiated)
        {
            LineNavigated?.Invoke(this, clamped);
        }
    }

    /// <summary>
    /// Fills in the find box with a global search query so its occurrences are highlighted. The find then lands on
    /// the first match at or after the line just navigated to, which is the hit itself.
    /// </summary>
    public void Highlight(SearchHighlight highlight)
    {
        MatchCase = highlight.MatchCase;
        FindText = highlight.Text;
    }

    public bool NavigateTo(DiagnosticLocation location)
    {
        if (location.Kind != DiagnosticLocationKind.Line || location.NumericPosition is not { } line)
        {
            return false;
        }

        NavigateToLine((int)Math.Min(line, int.MaxValue));
        return true;
    }

    /// <summary>Text for the clipboard from the selected lines, in file order. Capped so a huge selection cannot exhaust memory.</summary>
    public string BuildCopyText(IEnumerable<LineViewModel> selected)
    {
        var ordered = selected.OrderBy(l => l.LineNumber).Take(MaxCopyLines);
        return string.Join(Environment.NewLine, ordered.Select(l => l.Text));
    }
}
