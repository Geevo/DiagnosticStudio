using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.App.ViewModels;

public enum ExplorerMode
{
    Files,
    Logical,
}

public sealed partial class ExplorerNodeViewModel : ObservableObject
{
    private readonly Action<ExplorerNodeViewModel>? _selected;

    public ExplorerNodeViewModel(ArtifactTreeNode source, bool showCount, Action<ExplorerNodeViewModel>? selected = null)
    {
        _selected = selected;
        Artifact = source.Artifact;
        Name = source.Name;
        Children = new ObservableCollection<ExplorerNodeViewModel>(
            source.Children.Select(c => new ExplorerNodeViewModel(c, showCount: false, selected)));

        DisplayName = showCount && Children.Count > 0 ? $"{source.Name} ({Children.Count:N0})" : source.Name;
        Glyph = ChooseGlyph(source);
        ToolTip = Artifact?.ProvenanceDisplay;
    }

    public DiagnosticArtifact? Artifact { get; }

    /// <summary>The file or folder name as it is in the bundle (<see cref="DisplayName"/> may add a count).</summary>
    public string Name { get; }

    public string DisplayName { get; }
    public string Glyph { get; }
    public string? ToolTip { get; }
    public ObservableCollection<ExplorerNodeViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _selected?.Invoke(this);
        }
    }

    /// <summary>
    /// Keeps only what matches <paramref name="text"/> (part of a name, any case): a node whose own name matches stays
    /// with everything under it; otherwise it stays only if something below it matches. Branches that stay are
    /// expanded. Returns whether this node stays; <paramref name="files"/> counts the files that match.
    /// </summary>
    internal bool ApplyFilter(string text, ref int files)
    {
        if (Name.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            // A folder that matches is opened, so what is in it is in view.
            files += CountFiles();
            if (Children.Count > 0)
            {
                IsExpanded = true;
            }

            return true;
        }

        var kept = false;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i].ApplyFilter(text, ref files))
            {
                kept = true;
            }
            else
            {
                Children.RemoveAt(i);
            }
        }

        if (kept)
        {
            IsExpanded = true;
        }

        return kept;
    }

    private int CountFiles() => (Artifact is null ? 0 : 1) + Children.Sum(c => c.CountFiles());

    internal void SetExpandedRecursively(bool expanded)
    {
        if (Children.Count > 0)
        {
            IsExpanded = expanded;
        }

        foreach (var child in Children)
        {
            child.SetExpandedRecursively(expanded);
        }
    }

    private static string ChooseGlyph(ArtifactTreeNode node)
    {
        if (node.Artifact is null)
        {
            return ""; // folder
        }

        return node.Artifact.ArtifactType == ArtifactType.Archive ? "" : ""; // package / page
    }
}

/// <summary>Files (exact hierarchy) and Logical (grouped by meaning) views over the same artifacts.</summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _workspace;
    private readonly DocumentHostViewModel _documents;
    private readonly IExternalToolService? _tools;
    private readonly IClipboardService? _clipboard;
    private readonly IOutputLog? _output;

    public ExplorerViewModel(
        WorkspaceViewModel workspace,
        DocumentHostViewModel documents,
        IExternalToolService? tools = null,
        IClipboardService? clipboard = null,
        IOutputLog? output = null)
    {
        _workspace = workspace;
        _documents = documents;
        _tools = tools;
        _clipboard = clipboard;
        _output = output;
        _workspace.WorkspaceChanged += (_, _) =>
        {
            // A filter belongs to the bundle it was typed for. Clearing it rebuilds; otherwise rebuild here.
            if (string.IsNullOrEmpty(FilterText))
            {
                Rebuild();
            }
            else
            {
                FilterText = string.Empty;
            }

            OpenTimelineCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<ExplorerNodeViewModel> Nodes { get; } = new();

    /// <summary>Part of a file or folder name; the tree shows only what matches.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>"12 files match" while a filter is on.</summary>
    [ObservableProperty]
    private string _filterStatus = string.Empty;

    /// <summary>The most recent filter run; lets callers and tests await it.</summary>
    public Task PendingFilter { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _filterCts;

    partial void OnFilterTextChanged(string value)
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        PendingFilter = RefilterAsync(string.IsNullOrWhiteSpace(value), _filterCts.Token);
    }

    private async Task RefilterAsync(bool immediate, CancellationToken token)
    {
        try
        {
            // Typing is debounced; clearing the box is not.
            if (!immediate)
            {
                await Task.Delay(FilterDebounceMilliseconds, token).ConfigureAwait(true);
            }

            Rebuild();
        }
        catch (OperationCanceledException)
        {
            // A newer filter replaced this one.
        }
    }

    private const int FilterDebounceMilliseconds = 200;

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var node in Nodes)
        {
            node.SetExpandedRecursively(true);
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes)
        {
            node.SetExpandedRecursively(false);
        }
    }

    [ObservableProperty]
    private ExplorerMode _mode = ExplorerMode.Files;

    public bool IsFilesMode => Mode == ExplorerMode.Files;
    public bool IsLogicalMode => Mode == ExplorerMode.Logical;

    partial void OnModeChanged(ExplorerMode value)
    {
        OnPropertyChanged(nameof(IsFilesMode));
        OnPropertyChanged(nameof(IsLogicalMode));
        Rebuild();
    }

    [RelayCommand]
    private void ShowFiles() => Mode = ExplorerMode.Files;

    [RelayCommand]
    private void ShowLogical() => Mode = ExplorerMode.Logical;

    /// <summary>Double-click or Enter: open the file and keep its tab.</summary>
    [RelayCommand]
    private void OpenNode(ExplorerNodeViewModel? node)
    {
        if (node?.Artifact is { } artifact)
        {
            _filePreviewCts?.Cancel();
            _documents.OpenArtifact(artifact);
        }
    }

    // ---- preview on selection ----

    private const int PreviewDelayMilliseconds = 250;
    private CancellationTokenSource? _filePreviewCts;

    /// <summary>The preview waiting to open; lets callers and tests await it.</summary>
    public Task PendingPreview { get; private set; } = Task.CompletedTask;

    /// <summary>Selecting a file by arrow keys passes over many; only the one the selection rests on is opened.</summary>
    private void OnNodeSelected(ExplorerNodeViewModel node)
    {
        if (node.Artifact is null)
        {
            return;
        }

        _filePreviewCts?.Cancel();
        _filePreviewCts = new CancellationTokenSource();
        PendingPreview = PreviewAfterDelayAsync(node, _filePreviewCts.Token);
    }

    private async Task PreviewAfterDelayAsync(ExplorerNodeViewModel node, CancellationToken token)
    {
        try
        {
            await Task.Delay(PreviewDelayMilliseconds, token).ConfigureAwait(true);
            Preview(node);
        }
        catch (OperationCanceledException)
        {
            // The selection moved on.
        }
    }

    /// <summary>A single click: show the file in the preview tab at once.</summary>
    [RelayCommand]
    private void PreviewNode(ExplorerNodeViewModel? node)
    {
        if (node?.Artifact is null)
        {
            return;
        }

        _filePreviewCts?.Cancel();
        Preview(node);
    }

    private void Preview(ExplorerNodeViewModel node)
    {
        if (node.Artifact is { } artifact)
        {
            _documents.OpenArtifact(artifact, preview: true);
        }
    }

    [RelayCommand]
    private void OpenOverview() => _documents.ShowOverview();

    [RelayCommand]
    private void OpenRules() => _documents.ShowRules();

    // ---- right-click actions on a file ----

    /// <summary>Notepad++ is installed, so the menu offers it.</summary>
    public bool HasNotepadPlusPlus => _tools?.NotepadPlusPlusPath is not null;

    private static bool IsFile(ExplorerNodeViewModel? node) => node?.Artifact?.ExtractedPath is not null;

    private static bool IsArtifact(ExplorerNodeViewModel? node) => node?.Artifact is not null;

    [RelayCommand(CanExecute = nameof(IsFile))]
    private void OpenInNotepad(ExplorerNodeViewModel? node) => RunTool(node, (tools, path) => tools.OpenInNotepad(path));

    [RelayCommand(CanExecute = nameof(IsFile))]
    private void OpenInNotepadPlusPlus(ExplorerNodeViewModel? node) => RunTool(node, (tools, path) => tools.OpenInNotepadPlusPlus(path));

    [RelayCommand(CanExecute = nameof(IsFile))]
    private void ShowInFileExplorer(ExplorerNodeViewModel? node) => RunTool(node, (tools, path) => tools.ShowInExplorer(path));

    private void RunTool(ExplorerNodeViewModel? node, Action<IExternalToolService, string> action)
    {
        if (_tools is null || node?.Artifact?.ExtractedPath is not { } path)
        {
            return;
        }

        try
        {
            action(_tools, path);
        }
        catch (ExternalToolException ex)
        {
            _output?.Write(OutputSeverity.Warning, "Explorer", ex.Message);
        }
    }

    /// <summary>The working copy of the file on this machine (the original itself when a folder was opened).</summary>
    [RelayCommand(CanExecute = nameof(IsFile))]
    private void CopyPath(ExplorerNodeViewModel? node) => Copy(node?.Artifact?.ExtractedPath);

    /// <summary>The route through the bundle: archive, folders, file.</summary>
    [RelayCommand(CanExecute = nameof(IsArtifact))]
    private void CopyBundlePath(ExplorerNodeViewModel? node) => Copy(node?.Artifact?.ProvenanceDisplay);

    [RelayCommand(CanExecute = nameof(IsArtifact))]
    private void CopyName(ExplorerNodeViewModel? node) => Copy(node?.Artifact?.Name);

    /// <summary>The <c>artifact://</c> link that opens this file again from anywhere in the tool.</summary>
    [RelayCommand(CanExecute = nameof(IsArtifact))]
    private void CopyLink(ExplorerNodeViewModel? node) =>
        Copy(node?.Artifact is { } artifact ? DiagnosticLocation.ForArtifact(artifact.Id).ToString() : null);

    private void Copy(string? text)
    {
        if (!string.IsNullOrEmpty(text) && _clipboard is not null && !_clipboard.SetText(text))
        {
            _output?.Write(OutputSeverity.Warning, "Explorer", "The clipboard is in use by another program; try again.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasBundle))]
    private void OpenTimeline() => _documents.ShowTimeline();

    private bool HasBundle() => _documents.HasBundle;

    private void Rebuild()
    {
        Nodes.Clear();
        FilterStatus = string.Empty;
        if (_workspace.Current is not { } workspace)
        {
            return;
        }

        var showCount = Mode == ExplorerMode.Logical;
        var tree = showCount
            ? ArtifactTree.BuildLogical(workspace.Artifacts)
            : ArtifactTree.BuildFiles(workspace.Artifacts);

        var filter = FilterText.Trim();
        var matches = 0;
        foreach (var root in tree)
        {
            var node = new ExplorerNodeViewModel(root, showCount, OnNodeSelected);
            node.IsExpanded = true;
            if (filter.Length == 0 || node.ApplyFilter(filter, ref matches))
            {
                Nodes.Add(node);
            }
        }

        if (filter.Length > 0)
        {
            FilterStatus = matches switch
            {
                0 => "No files match",
                1 => "1 file matches",
                _ => matches.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " files match",
            };
        }
    }
}
