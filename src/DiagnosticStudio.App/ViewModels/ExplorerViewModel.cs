using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Artifacts;

namespace DiagnosticStudio.App.ViewModels;

public enum ExplorerMode
{
    Files,
    Logical,
}

public sealed partial class ExplorerNodeViewModel : ObservableObject
{
    public ExplorerNodeViewModel(ArtifactTreeNode source, bool showCount)
    {
        Artifact = source.Artifact;
        Children = new ObservableCollection<ExplorerNodeViewModel>(
            source.Children.Select(c => new ExplorerNodeViewModel(c, showCount: false)));

        DisplayName = showCount && Children.Count > 0 ? $"{source.Name} ({Children.Count:N0})" : source.Name;
        Glyph = ChooseGlyph(source);
        ToolTip = Artifact?.ProvenanceDisplay;
    }

    public DiagnosticArtifact? Artifact { get; }
    public string DisplayName { get; }
    public string Glyph { get; }
    public string? ToolTip { get; }
    public ObservableCollection<ExplorerNodeViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

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

    public ExplorerViewModel(WorkspaceViewModel workspace, DocumentHostViewModel documents)
    {
        _workspace = workspace;
        _documents = documents;
        _workspace.WorkspaceChanged += (_, _) =>
        {
            Rebuild();
            OpenTimelineCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<ExplorerNodeViewModel> Nodes { get; } = new();

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

    [RelayCommand]
    private void OpenNode(ExplorerNodeViewModel? node)
    {
        if (node?.Artifact is { } artifact)
        {
            _documents.OpenArtifact(artifact);
        }
    }

    [RelayCommand]
    private void OpenOverview() => _documents.ShowOverview();

    [RelayCommand(CanExecute = nameof(HasBundle))]
    private void OpenTimeline() => _documents.ShowTimeline();

    private bool HasBundle() => _documents.HasBundle;

    private void Rebuild()
    {
        Nodes.Clear();
        if (_workspace.Current is not { } workspace)
        {
            return;
        }

        var showCount = Mode == ExplorerMode.Logical;
        var tree = showCount
            ? ArtifactTree.BuildLogical(workspace.Artifacts)
            : ArtifactTree.BuildFiles(workspace.Artifacts);

        foreach (var root in tree)
        {
            var node = new ExplorerNodeViewModel(root, showCount);
            node.IsExpanded = true;
            Nodes.Add(node);
        }
    }
}
