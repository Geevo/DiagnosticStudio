using DiagnosticStudio.Core.Parsing;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Timeline;
using DiagnosticStudio.App.ViewModels.Timeline;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>Tabbed document workspace: open, activate, close and navigate documents.</summary>
public sealed partial class DocumentHostViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _workspace;
    private readonly IDocumentLoader _loader;
    private readonly INavigationHistory _history;
    private readonly IOutputLog _output;

    // Set while history navigation is activating a document, so that activation is not recorded as a new entry.
    private bool _isNavigating;

    // Shared parsed documents of the previous bundle are released as soon as the bundle changes. This host is created
    // before the services that start background work on a new bundle, so its handler runs first.
    private readonly IDocumentCache? _cache;
    private readonly ITimelineService? _timelineService;
    private TimelineDocumentViewModel? _timeline;

    public DocumentHostViewModel(
        WorkspaceViewModel workspace,
        IDocumentLoader loader,
        INavigationHistory history,
        IOutputLog output,
        IDocumentCache? cache = null,
        ITimelineService? timeline = null)
    {
        _workspace = workspace;
        _cache = cache;
        _timelineService = timeline;
        _loader = loader;
        _history = history;
        _output = output;

        _workspace.WorkspaceChanged += (_, _) =>
        {
            _cache?.Clear();
            Reset();
        };
        _history.Changed += (_, _) =>
        {
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
        };

        Reset();
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    [ObservableProperty]
    private DocumentViewModel? _activeDocument;

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        if (!_isNavigating && value?.Location is { } location)
        {
            _history.Record(location);
        }
    }

    public void ShowOverview()
    {
        var overview = Documents.OfType<OverviewDocumentViewModel>().FirstOrDefault();
        if (overview is null)
        {
            overview = new OverviewDocumentViewModel(_workspace.Current);
            Documents.Insert(0, overview);
        }

        ActiveDocument = overview;
    }

    /// <summary>
    /// Opens the timeline tab, building it the first time. One timeline exists per bundle: closing the tab keeps it, so
    /// opening it again is instant. <c>null</c> when no bundle is open.
    /// </summary>
    public TimelineDocumentViewModel? ShowTimeline()
    {
        if (_timeline is null)
        {
            if (_timelineService is null || _workspace.Current is not { } workspace)
            {
                return null;
            }

            _timeline = new TimelineDocumentViewModel(workspace.Artifacts, _timelineService, location => OpenLocation(location), _output);
        }

        if (!Documents.Contains(_timeline))
        {
            Documents.Add(_timeline);
        }

        ActiveDocument = _timeline;
        return _timeline;
    }

    /// <summary>Shows the timeline at the moment <paramref name="location"/> happened, if it has a timestamp.</summary>
    public async Task<bool> ShowInTimelineAsync(DiagnosticLocation location)
    {
        var timeline = ShowTimeline();
        if (timeline is null)
        {
            return false;
        }

        var found = await timeline.ShowLocationAsync(location).ConfigureAwait(true);
        if (!found)
        {
            _output.Write(
                OutputSeverity.Information,
                "Timeline",
                "That place has no timestamp, so it is not on the timeline: " + location);
        }

        return found;
    }

    public bool HasBundle => _workspace.Current is not null;

    /// <summary>
    /// Shows the timeline at the line or event selected in the active document; with no selection (or in a document
    /// that has no timestamps), just opens the timeline.
    /// </summary>
    public Task ShowActiveInTimelineAsync()
    {
        if (ActiveDocument is ArtifactDocumentViewModel { Viewer: ICurrentPosition viewer } document
            && viewer.CurrentPosition(document.Artifact.Id) is { } location)
        {
            return ShowInTimelineAsync(location);
        }

        ShowTimeline();
        return Task.CompletedTask;
    }

    public void OpenArtifact(DiagnosticArtifact artifact)
    {
        var existing = Documents
            .OfType<ArtifactDocumentViewModel>()
            .FirstOrDefault(d => d.Artifact.Id == artifact.Id);
        if (existing is not null)
        {
            ActiveDocument = existing;
            return;
        }

        var document = new ArtifactDocumentViewModel(artifact);
        document.LocationNavigated += (_, location) => _history.Record(location);
        Documents.Add(document);
        ActiveDocument = document;
        _ = document.LoadAsync(_loader, _output, CancellationToken.None);
    }

    /// <summary>Opens whatever <paramref name="location"/> points at. Viewers will scroll/select once they exist.</summary>
    public bool OpenLocation(DiagnosticLocation location, SearchHighlight? highlight = null)
    {
        var artifact = _workspace.Current?.Find(location.ArtifactId);
        if (artifact is null)
        {
            _output.Write(OutputSeverity.Warning, "Navigation", "Location not found in this investigation: " + location);
            return false;
        }

        OpenArtifact(artifact);
        if (location.Kind != DiagnosticLocationKind.Artifact
            && Documents.OfType<ArtifactDocumentViewModel>().FirstOrDefault(d => d.Artifact.Id == artifact.Id) is { } document)
        {
            document.NavigateTo(location, highlight);
        }

        return true;
    }

    [RelayCommand]
    private void Close(DocumentViewModel? document)
    {
        document ??= ActiveDocument;
        if (document is null || !document.CanClose)
        {
            return;
        }

        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        if (ReferenceEquals(ActiveDocument, document) || ActiveDocument is null)
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        }
    }

    [RelayCommand]
    private void CloseOthers(DocumentViewModel? keep)
    {
        keep ??= ActiveDocument;
        foreach (var document in Documents.Where(d => d.CanClose && !ReferenceEquals(d, keep)).ToList())
        {
            Documents.Remove(document);
        }

        ActiveDocument = keep ?? Documents.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Navigate(_history.GoBack());

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Navigate(_history.GoForward());

    private bool CanGoBack() => _history.CanGoBack;

    private bool CanGoForward() => _history.CanGoForward;

    private void Navigate(DiagnosticLocation? location)
    {
        if (location is null)
        {
            return;
        }

        _isNavigating = true;
        try
        {
            OpenLocation(location);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private void Reset()
    {
        _timeline?.Cancel();
        _timeline = null;
        Documents.Clear();
        ActiveDocument = null;
        _history.Clear();
        ShowOverview();
    }
}
