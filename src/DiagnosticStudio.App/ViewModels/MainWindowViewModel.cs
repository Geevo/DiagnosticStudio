using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels.Search;

namespace DiagnosticStudio.App.ViewModels;

public enum BottomPanelTab
{
    Problems = 0,
    SearchResults = 1,
    Output = 2,
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IFileDialogService _dialogs;

    public MainWindowViewModel(
        WorkspaceViewModel workspace,
        ExplorerViewModel explorer,
        DocumentHostViewModel documents,
        ProblemsViewModel problems,
        SearchResultsViewModel searchResults,
        OutputViewModel output,
        StatusBarViewModel statusBar,
        ZoomViewModel zoom,
        IFileDialogService dialogs)
    {
        Workspace = workspace;
        Explorer = explorer;
        Documents = documents;
        Problems = problems;
        SearchResults = searchResults;
        Output = output;
        StatusBar = statusBar;
        Zoom = zoom;
        _dialogs = dialogs;

        // A new search brings the results panel forward.
        SearchResults.SearchStarted += (_, _) => SelectedBottomTabIndex = (int)BottomPanelTab.SearchResults;

        Workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkspaceViewModel.BundleName))
            {
                OnPropertyChanged(nameof(Title));
            }
        };
    }

    public WorkspaceViewModel Workspace { get; }
    public ExplorerViewModel Explorer { get; }
    public DocumentHostViewModel Documents { get; }
    public ProblemsViewModel Problems { get; }
    public SearchResultsViewModel SearchResults { get; }
    public OutputViewModel Output { get; }
    public StatusBarViewModel StatusBar { get; }
    public ZoomViewModel Zoom { get; }

    public string Title => Workspace.BundleName is { } name
        ? $"{name} — Diagnostic Studio"
        : "Diagnostic Studio";

    /// <summary>Index of the selected bottom tool panel tab; see <see cref="BottomPanelTab"/>.</summary>
    [ObservableProperty]
    private int _selectedBottomTabIndex = (int)BottomPanelTab.Problems;

    [RelayCommand]
    private Task OpenBundle()
    {
        var path = _dialogs.PickBundleFile();
        return path is null ? Task.CompletedTask : Workspace.OpenAsync(path);
    }

    [RelayCommand]
    private Task OpenFolder()
    {
        var path = _dialogs.PickBundleFolder();
        return path is null ? Task.CompletedTask : Workspace.OpenAsync(path);
    }

    /// <summary>Entry point for drag/drop and command-line style opens.</summary>
    [RelayCommand]
    private Task OpenPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : Workspace.OpenAsync(path);

    [RelayCommand]
    private void CloseBundle() => Workspace.Close();

    [RelayCommand]
    private Task ShowInTimeline() => Documents.ShowActiveInTimelineAsync();

    [RelayCommand]
    private void ShowProblems() => SelectedBottomTabIndex = (int)BottomPanelTab.Problems;

    /// <summary>Raised when the shell should put the keyboard focus in the global search box.</summary>
    public event EventHandler? SearchFocusRequested;

    [RelayCommand]
    private void FocusSearch()
    {
        SelectedBottomTabIndex = (int)BottomPanelTab.SearchResults;
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowSearchResults() => SelectedBottomTabIndex = (int)BottomPanelTab.SearchResults;

    [RelayCommand]
    private void ShowOutput() => SelectedBottomTabIndex = (int)BottomPanelTab.Output;
}
