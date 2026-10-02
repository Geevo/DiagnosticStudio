using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Parsers.Structured;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.App.ViewModels.StructuredViewer;

/// <summary>XML / JSON viewer: node tree, detail pane, find, and a raw source tab.</summary>
public sealed partial class StructuredViewerViewModel : ObservableObject, ILocationNavigable, ISearchHighlightable
{
    public const int StructureTab = 0;
    public const int RawTab = 1;

    private const int FindDebounceMilliseconds = 250;

    // Files written on one line are hard to read raw, so the Raw tab can show an indented copy. It is derived text with
    // its own line numbers; the original is one toggle away and is what every stored line link points into.
    private TextViewerViewModel? _formattedRaw;
    private StructuredDocument? _formattedDocument;
    private SearchHighlight? _lastHighlight;
    private CancellationTokenSource? _formatCts;

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

        if (document.ReadProblem is { } problem)
        {
            ReadProblemText = "Only part of this file could be read: " + problem
                + " The tree shows what came before that point; the Raw tab has everything.";
            InfoText += " · partly read";
        }

        // Open on the first item, expanded, so the pane is never blank.
        if (Roots.Count > 0)
        {
            Roots[0].IsExpanded = true;
            Roots[0].IsSelected = true;
        }

        // A dense file (long lines holding many nodes) opens indented.
        PrettyRaw = LooksMinified(document);
    }

    /// <summary>Few lines for the number of nodes: written without indentation, so the raw text is one wall of characters.</summary>
    internal static bool LooksMinified(StructuredDocument document)
    {
        var lines = Math.Max(1, document.RawSource.LineCount);
        return document.RawSource.ByteLength / lines > 160 && document.NodeCount / lines > 20;
    }

    public StructuredDocument Document { get; }

    /// <summary>The complete file as text exactly as collected; always available.</summary>
    public TextViewerViewModel Raw { get; }

    /// <summary>What the Raw tab shows: the indented copy when it is on and ready, otherwise the original.</summary>
    public TextViewerViewModel ActiveRaw => PrettyRaw && _formattedRaw is not null ? _formattedRaw : Raw;

    private bool IsShowingFormatted => PrettyRaw && _formattedRaw is not null && _formattedDocument is not null;

    /// <summary>The Raw tab shows an indented copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRaw))]
    private bool _prettyRaw;

    /// <summary>The file could be re-indented (it is valid and readable).</summary>
    [ObservableProperty]
    private bool _prettyAvailable = true;

    [ObservableProperty]
    private string _prettyStatus = string.Empty;

    /// <summary>The indented copy being built; lets callers and tests await it.</summary>
    public Task PendingFormat { get; private set; } = Task.CompletedTask;

    public ObservableCollection<StructuredNodeViewModel> Roots { get; } = new();

    public string InfoText { get; }

    /// <summary>Why only part of the file is in the tree, or <c>null</c> when all of it is.</summary>
    public string? ReadProblemText { get; }

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
                var atLine = Document.NodeAtLine(clamped);

                // The line is a line of the file as collected. In the indented copy it is wherever its node went.
                if (IsShowingFormatted && atLine is not null)
                {
                    ActiveRaw.NavigateToLine(RawLineOf(atLine));
                }
                else
                {
                    ActiveRaw.NavigateToLine(clamped);
                }

                if (atLine is not null)
                {
                    Reveal(atLine, scroll: false);
                }

                return true;

            default:
                return false;
        }
    }

    public void Highlight(SearchHighlight highlight)
    {
        _lastHighlight = highlight;
        Raw.Highlight(highlight);
        _formattedRaw?.Highlight(highlight);
    }

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
        var line = SelectedNode?.Node is { } node ? RawLineOf(node) : 0;
        SelectedTabIndex = RawTab;
        if (line > 0)
        {
            ActiveRaw.NavigateToLine(line);
        }
    }

    // ---- indented copy of the raw text ----

    /// <summary>Where a node starts in the text the Raw tab is showing.</summary>
    private int RawLineOf(StructuredNode node) =>
        IsShowingFormatted && _formattedDocument!.FindNode(Document.PathOf(node)) is { } mapped ? mapped.Line : node.Line;

    partial void OnPrettyRawChanged(bool value)
    {
        if (!value)
        {
            SyncRawToSelection();
            return;
        }

        if (_formattedRaw is null)
        {
            StartFormatting();
        }
        else
        {
            SyncRawToSelection();
        }
    }

    /// <summary>After switching between the copies, keep the Raw tab on the selected node.</summary>
    private void SyncRawToSelection()
    {
        if (SelectedTabIndex == RawTab && SelectedNode?.Node is { } node)
        {
            ActiveRaw.NavigateToLine(RawLineOf(node));
        }
    }

    private void StartFormatting()
    {
        var path = Document.Artifact.ExtractedPath;
        if (path is null)
        {
            PrettyAvailable = false;
            PrettyRaw = false;
            PrettyStatus = "This file cannot be indented: there is no file on disk to read.";
            return;
        }

        _formatCts?.Cancel();
        _formatCts = new CancellationTokenSource();
        PrettyStatus = "Indenting...";
        PendingFormat = FormatAsync(path, _formatCts.Token);
    }

    private async Task FormatAsync(string path, CancellationToken token)
    {
        try
        {
            var format = Document.Format;
            var (text, root, count) = await Task.Run(
                () =>
                {
                    var bytes = File.ReadAllBytes(path);
                    var indented = format == StructuredFormat.Json
                        ? StructuredFormatter.FormatJson(bytes, token)
                        : StructuredFormatter.FormatXml(bytes, token);

                    // Parse the indented text too: its nodes know their lines in it, which is how the tree and the
                    // text stay linked.
                    var (parsedRoot, parsedCount) = format == StructuredFormat.Json
                        ? JsonStructureReader.Read(Encoding.UTF8.GetBytes(indented), token)
                        : XmlStructureReader.Read(new StringReader(indented), token);
                    return (indented, parsedRoot, parsedCount);
                },
                token).ConfigureAwait(true);

            token.ThrowIfCancellationRequested();
            var source = new InMemoryTextSource(text, "indented copy");
            _formattedDocument = new StructuredDocument
            {
                Artifact = Document.Artifact,
                Format = format,
                Root = root,
                RawSource = source,
                NodeCount = count,
            };
            _formattedRaw = new TextViewerViewModel(source);
            if (_lastHighlight is { } highlight)
            {
                _formattedRaw.Highlight(highlight);
            }

            PrettyStatus = string.Create(
                CultureInfo.CurrentCulture,
                $"Indented copy, {source.LineCount:N0} lines. Switch it off to see the file exactly as it was collected.");
            OnPropertyChanged(nameof(ActiveRaw));
            SyncRawToSelection();
        }
        catch (OperationCanceledException)
        {
            // Closed or replaced.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            PrettyAvailable = false;
            PrettyRaw = false;
            PrettyStatus = "This file cannot be indented: " + ex.Message;
        }
    }

    /// <summary>Stops the background work for the indented copy; called when the tab closes.</summary>
    public void Cancel() => _formatCts?.Cancel();

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
