using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.App.ViewModels.StructuredViewer;

/// <summary>XML / JSON viewer: node tree, detail pane, find, and a raw source tab.</summary>
public sealed partial class StructuredViewerViewModel : ObservableObject, ILocationNavigable, ISearchHighlightable
{
    public const int StructureTab = 0;
    public const int RawTab = 1;

    private const int FindDebounceMilliseconds = 250;

    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<StructuredMatch> _matches = Array.Empty<StructuredMatch>();
    private bool _matchesTruncated;
    private int _matchIndex = -1;

    public StructuredViewerViewModel(StructuredDocument document)
    {
        Document = document;
        Raw = new TextViewerViewModel(document.RawSource);

        // A synthetic document node (XML, JSON Lines) is not shown; its items are the roots.
        var tops = document.Root.Kind == StructuredNodeKind.Document ? document.Root.Children : new[] { document.Root };
        foreach (var top in tops)
        {
            Roots.Add(new StructuredNodeViewModel(top, document.Format, OnNodeSelected));
        }

        InfoText = string.Create(
            CultureInfo.CurrentCulture,
            $"{(document.Format == StructuredFormat.Xml ? "XML" : "JSON")} · {document.NodeCount:N0} nodes · {document.RawSource.LineCount:N0} lines · {document.RawSource.EncodingName}");

        // Open on the first item, expanded, so the pane is never blank.
        if (Roots.Count > 0)
        {
            Roots[0].IsExpanded = true;
            Roots[0].IsSelected = true;
        }
    }

    public StructuredDocument Document { get; }

    /// <summary>The complete file as text; always available.</summary>
    public TextViewerViewModel Raw { get; }

    public ObservableCollection<StructuredNodeViewModel> Roots { get; } = new();

    public string InfoText { get; }

    /// <summary>Raised after a node was selected from code (find, navigation) so the view can scroll the tree to it.</summary>
    public event EventHandler<StructuredNode>? NodeRevealRequested;

    [ObservableProperty]
    private StructuredNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _selectedPath = string.Empty;

    [ObservableProperty]
    private string _detailInfo = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex = StructureTab;

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public int MatchCount => _matches.Count;

    // ---- selection ----

    private void OnNodeSelected(StructuredNodeViewModel vm)
    {
        if (vm.Node is not { } node)
        {
            return;
        }

        SelectedNode = vm;
        var path = Document.PathOf(node);
        SelectedPath = path.Length == 0 ? "(root)" : path;
        DetailInfo = DescribeNode(node);
        DetailText = DetailOf(node);
    }

    private static string DescribeNode(StructuredNode node)
    {
        var kind = node.Kind switch
        {
            StructuredNodeKind.Object => "Object",
            StructuredNodeKind.Array => "Array",
            StructuredNodeKind.Element => "Element",
            _ => node.Kind.ToString(),
        };
        var parts = new List<string> { kind, "line " + node.Line.ToString("N0", CultureInfo.CurrentCulture) };

        if (node.IsValueTruncated)
        {
            parts.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"first {StructuredNode.MaxStoredValueChars:N0} of {node.ValueLength:N0} characters shown; the raw source has the rest"));
        }
        else if (node.Kind is StructuredNodeKind.String or StructuredNodeKind.Text or StructuredNodeKind.Comment or StructuredNodeKind.Attribute)
        {
            parts.Add(node.ValueLength.ToString("N0", CultureInfo.CurrentCulture) + " characters");
        }

        if (node.Kind is StructuredNodeKind.Object or StructuredNodeKind.Array)
        {
            parts.Add(node.Children.Count.ToString("N0", CultureInfo.CurrentCulture) + (node.Kind == StructuredNodeKind.Object ? " members" : " items"));
        }

        return string.Join(" · ", parts);
    }

    private static string DetailOf(StructuredNode node)
    {
        switch (node.Kind)
        {
            case StructuredNodeKind.Object:
            case StructuredNodeKind.Array:
            case StructuredNodeKind.Document:
                return string.Empty;
            case StructuredNodeKind.Element:
                var attributes = node.Children.Where(c => c.Kind == StructuredNodeKind.Attribute).ToList();
                var text = node.Children.Where(c => c.Kind == StructuredNodeKind.Text).Select(c => c.Value).ToList();
                var lines = new List<string>();
                lines.AddRange(attributes.Select(a => $"{a.Name} = \"{a.Value}\""));
                if (attributes.Count > 0 && text.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.AddRange(text.Select(t => t ?? string.Empty));
                return string.Join(Environment.NewLine, lines);
            default:
                return node.Value ?? string.Empty;
        }
    }

    // ---- navigation ----

    public bool NavigateTo(DiagnosticLocation location)
    {
        switch (location.Kind)
        {
            case DiagnosticLocationKind.XmlNode when Document.Format == StructuredFormat.Xml:
            case DiagnosticLocationKind.JsonNode when Document.Format == StructuredFormat.Json:
                if (Document.FindNode(location.Identifier ?? string.Empty) is not { } node)
                {
                    return false;
                }

                SelectedTabIndex = StructureTab;
                Reveal(node);
                return true;

            case DiagnosticLocationKind.Line when location.NumericPosition is { } line:
                // The line is a place in the raw text. Show it there, and keep the tree on the node it belongs to.
                var clamped = (int)Math.Min(line, int.MaxValue);
                SelectedTabIndex = RawTab;
                Raw.NavigateToLine(clamped);
                if (Document.NodeAtLine(clamped) is { } atLine)
                {
                    Reveal(atLine, scroll: false);
                }

                return true;

            default:
                return false;
        }
    }

    public void Highlight(SearchHighlight highlight) => Raw.Highlight(highlight);

    /// <summary>Expands the tree down to <paramref name="target"/> and selects it.</summary>
    public void Reveal(StructuredNode target, bool scroll = true)
    {
        // Text that is shown inline on its element has no row of its own: select the element.
        var node = target.Kind == StructuredNodeKind.Text && target.Parent is { } parent && StructuredNodeViewModel.IsInlineText(parent)
            ? parent
            : target;

        var chain = new List<StructuredNode>();
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current.Kind != StructuredNodeKind.Document)
            {
                chain.Add(current);
            }
        }

        chain.Reverse();

        StructuredNodeViewModel? vm = null;
        foreach (var step in chain)
        {
            vm = vm is null
                ? Roots.FirstOrDefault(r => ReferenceEquals(r.Node, step))
                : vm.FindChild(step);
            if (vm is null)
            {
                return;
            }

            if (!ReferenceEquals(step, node))
            {
                vm.IsExpanded = true;
            }
        }

        if (vm is null)
        {
            return;
        }

        vm.IsSelected = true;
        if (!ReferenceEquals(SelectedNode, vm))
        {
            OnNodeSelected(vm);
        }

        if (scroll)
        {
            NodeRevealRequested?.Invoke(this, node);
        }
    }

    [RelayCommand]
    private void ShowInRawSource()
    {
        var line = SelectedNode?.Node?.Line ?? 0;
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
                SetMatches(Array.Empty<StructuredMatch>(), truncated: false);
                return;
            }

            SearchStatus = "Searching...";
            if (debounce)
            {
                await Task.Delay(FindDebounceMilliseconds, token).ConfigureAwait(true);
            }

            var result = await StructuredSearch.FindAsync(Document, query, matchCase, token).ConfigureAwait(true);
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

    private void SetMatches(IReadOnlyList<StructuredMatch> matches, bool truncated)
    {
        _matches = matches;
        _matchesTruncated = truncated;
        _matchIndex = -1;
        UpdateSearchStatus();
        OnPropertyChanged(nameof(MatchCount));
    }

    private int FirstMatchAtOrAfterSelection()
    {
        var anchor = SelectedNode?.Node?.Line ?? 0;
        for (var i = 0; i < _matches.Count; i++)
        {
            if (_matches[i].Node.Line >= anchor)
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
        SelectedTabIndex = StructureTab;
        Reveal(_matches[index].Node);
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
}
