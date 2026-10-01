using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.App.ViewModels.RegistryViewer;

/// <summary>Registry-style viewer: key tree, value list, find, and a raw source tab.</summary>
public sealed partial class RegistryViewerViewModel : ObservableObject, ILocationNavigable
{
    public const int RegistryTab = 0;
    public const int RawTab = 1;

    private const int FindDebounceMilliseconds = 250;

    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<RegistryMatch> _matches = Array.Empty<RegistryMatch>();
    private bool _matchesTruncated;
    private int _matchIndex = -1;

    public RegistryViewerViewModel(RegistryDocument document)
    {
        Document = document;
        Raw = new TextViewerViewModel(document.RawSource);

        foreach (var hive in document.Root.Children.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(new RegistryKeyNodeViewModel(hive, OnKeySelected));
        }

        InfoText = BuildInfoText(document);
        WarningText = BuildWarningText(document);

        // Open on the first hive so the pane is never blank.
        if (Roots.Count > 0)
        {
            Roots[0].IsExpanded = true;
            Roots[0].IsSelected = true;
        }
    }

    public RegistryDocument Document { get; }

    /// <summary>The complete file as text; always available regardless of how well the file parsed.</summary>
    public TextViewerViewModel Raw { get; }

    public ObservableCollection<RegistryKeyNodeViewModel> Roots { get; } = new();

    public string InfoText { get; }

    /// <summary>Parse problems or truncation; <c>null</c> when the file parsed cleanly.</summary>
    public string? WarningText { get; }

    /// <summary>Raised when the view should scroll the value list to <see cref="SelectedValue"/>.</summary>
    public event EventHandler? ValueRevealRequested;

    /// <summary>Raised after a key was selected from code (find, navigation) so the view can scroll the tree to it.</summary>
    public event EventHandler<RegistryKey>? KeyRevealRequested;

    [ObservableProperty]
    private RegistryKeyNodeViewModel? _selectedKey;

    [ObservableProperty]
    private IReadOnlyList<RegistryValue> _values = Array.Empty<RegistryValue>();

    [ObservableProperty]
    private RegistryValue? _selectedValue;

    [ObservableProperty]
    private string _selectedKeyPath = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex = RegistryTab;

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public int MatchCount => _matches.Count;

    public IReadOnlyList<RegistryMatch> Matches => _matches;

    // ---- selection ----

    private void OnKeySelected(RegistryKeyNodeViewModel node)
    {
        if (node.Key is not { } key)
        {
            return;
        }

        SelectedKey = node;
        SelectedKeyPath = key.FullPath;
        Values = key.Values;
        SelectedValue = null;
    }

    partial void OnSelectedValueChanged(RegistryValue? value) => DetailText = BuildDetail(value);

    private static string BuildDetail(RegistryValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value.DetailText is not null)
        {
            return value.DetailText;
        }

        return value.Data is not null ? HexDump(value) : value.DisplayValue;
    }

    internal static string HexDump(RegistryValue value)
    {
        var data = value.Data!;
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var count = Math.Min(16, data.Length - offset);
            sb.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < count ? data[offset + i].ToString("x2", CultureInfo.InvariantCulture) : "  ").Append(' ');
                if (i == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(' ');
            for (var i = 0; i < count; i++)
            {
                var b = data[offset + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.AppendLine();
        }

        if (value.IsDataTruncated)
        {
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"... first {data.Length:N0} of {value.DataLength:N0} bytes shown. See the raw source for the rest."));
        }

        return sb.ToString().TrimEnd();
    }

    // ---- navigation ----

    public bool NavigateTo(DiagnosticLocation location)
    {
        switch (location.Kind)
        {
            case DiagnosticLocationKind.Registry when location.Identifier is { } keyPath:
                if (Document.FindKey(keyPath) is not { } key)
                {
                    return false;
                }

                SelectedTabIndex = RegistryTab;
                RevealKey(key);
                if (location.Member is { } valueName)
                {
                    var value = key.FindValue(valueName);
                    if (value is null)
                    {
                        return false;
                    }

                    SelectValue(value);
                }

                return true;

            case DiagnosticLocationKind.Line when location.NumericPosition is { } line:
                SelectedTabIndex = RawTab;
                Raw.NavigateToLine((int)Math.Min(line, int.MaxValue));
                return true;

            default:
                return false;
        }
    }

    /// <summary>Expands the tree down to <paramref name="key"/> and selects it.</summary>
    public void RevealKey(RegistryKey key)
    {
        var chain = key.Ancestors().Reverse().Append(key).ToList();

        RegistryKeyNodeViewModel? node = null;
        foreach (var step in chain)
        {
            node = node is null
                ? Roots.FirstOrDefault(r => ReferenceEquals(r.Key, step))
                : node.FindChild(step);
            if (node is null)
            {
                return;
            }

            if (!ReferenceEquals(step, key))
            {
                node.IsExpanded = true;
            }
        }

        if (node is not null)
        {
            node.IsSelected = true;
            if (!ReferenceEquals(SelectedKey, node))
            {
                OnKeySelected(node);
            }

            KeyRevealRequested?.Invoke(this, key);
        }
    }

    private void SelectValue(RegistryValue value)
    {
        SelectedValue = value;
        ValueRevealRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowInRawSource()
    {
        var line = SelectedValue?.SourceLine ?? SelectedKey?.Key?.SourceLine ?? 0;
        SelectedTabIndex = RawTab;
        if (line > 0)
        {
            Raw.NavigateToLine(line);
        }
    }

    // ---- find ----

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
                SetMatches(Array.Empty<RegistryMatch>(), truncated: false);
                return;
            }

            SearchStatus = "Searching...";
            if (debounce)
            {
                await Task.Delay(FindDebounceMilliseconds, token).ConfigureAwait(true);
            }

            var result = await RegistrySearch.FindAsync(Document, query, matchCase, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            SetMatches(result.Matches, result.Truncated);

            if (_matches.Count > 0)
            {
                Jump(FirstMatchAtOrAfterSelection());
            }
        }
        catch (OperationCanceledException)
        {
            // A newer query owns the status now.
        }
    }

    private void SetMatches(IReadOnlyList<RegistryMatch> matches, bool truncated)
    {
        _matches = matches;
        _matchesTruncated = truncated;
        _matchIndex = -1;
        UpdateSearchStatus();
        OnPropertyChanged(nameof(MatchCount));
    }

    private int FirstMatchAtOrAfterSelection()
    {
        var anchor = SelectedValue?.SourceLine ?? SelectedKey?.Key?.SourceLine ?? 0;
        for (var i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];
            if ((match.Value?.SourceLine ?? match.Key.SourceLine) >= anchor)
            {
                return i;
            }
        }

        return 0;
    }

    [RelayCommand]
    private void FindNext()
    {
        if (_matches.Count > 0)
        {
            Jump((_matchIndex + 1) % _matches.Count);
        }
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (_matches.Count > 0)
        {
            Jump(_matchIndex <= 0 ? _matches.Count - 1 : _matchIndex - 1);
        }
    }

    private void Jump(int index)
    {
        _matchIndex = index;
        var match = _matches[index];
        SelectedTabIndex = RegistryTab;
        RevealKey(match.Key);
        if (match.Value is not null)
        {
            SelectValue(match.Value);
        }

        UpdateSearchStatus();
    }

    private void UpdateSearchStatus()
    {
        if (string.IsNullOrEmpty(FindText))
        {
            SearchStatus = string.Empty;
            return;
        }

        if (_matches.Count == 0)
        {
            SearchStatus = "No matches";
            return;
        }

        var count = _matches.Count.ToString("N0", CultureInfo.CurrentCulture) + (_matchesTruncated ? "+" : string.Empty);
        SearchStatus = _matchIndex >= 0 ? $"{_matchIndex + 1:N0} of {count}" : $"{count} matches";
    }

    // ---- info ----

    private static string BuildInfoText(RegistryDocument doc)
    {
        var header = doc.FormatHeader ?? "No .reg header";
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{header} · {doc.KeyCount:N0} keys · {doc.ValueCount:N0} values · {doc.RawSource.EncodingName}");
    }

    private static string? BuildWarningText(RegistryDocument doc)
    {
        var parts = new List<string>();
        if (doc.TotalIssueCount > 0)
        {
            var first = doc.Issues.Count > 0 ? $" First at line {doc.Issues[0].Line:N0}." : string.Empty;
            parts.Add($"{doc.TotalIssueCount:N0} lines could not be parsed.{first} They are visible in the raw source.");
        }

        if (doc.IsTruncated)
        {
            parts.Add($"Parsing stopped at line {doc.TruncatedAtLine:N0} (size limit). Later content is only in the raw source.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
