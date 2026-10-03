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
    /// <summary>The timeline of the open bundle, once it has been opened; kept when its tab is closed.</summary>
    [ObservableProperty]
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

    /// <summary>Whether the Overview or the Timeline tab is the one showing, so their buttons can say which view is open.</summary>
    public bool IsOverviewActive => ActiveDocument is OverviewDocumentViewModel;

    public bool IsTimelineActive => ActiveDocument is TimelineDocumentViewModel;

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        OnPropertyChanged(nameof(IsOverviewActive));
        OnPropertyChanged(nameof(IsTimelineActive));
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
        if (Timeline is null)
        {
            if (_timelineService is null || _workspace.Current is not { } workspace)
            {
                return null;
            }

            Timeline = new TimelineDocumentViewModel(workspace.Artifacts, _timelineService, location => OpenLocation(location), _output);
        }

        if (!Documents.Contains(Timeline))
        {
            Documents.Add(Timeline);
        }

        ActiveDocument = Timeline;
        return Timeline;
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

    /// <summary>
    /// Opens a file in a tab. With <paramref name="preview"/> it opens as a preview tab that the next preview replaces;
    /// otherwise the tab stays, and a preview of the same file is kept (pinned).
    /// </summary>
    public void OpenArtifact(DiagnosticArtifact artifact, bool preview = false)
    {
        var existing = Documents
            .OfType<ArtifactDocumentViewModel>()
            .FirstOrDefault(d => d.Artifact.Id == artifact.Id);
        if (existing is not null)
        {
            if (!preview)
            {
                existing.IsPreview = false;
            }

            ActiveDocument = existing;
            return;
        }

        var document = new ArtifactDocumentViewModel(artifact) { IsPreview = preview };
        document.LocationNavigated += (_, location) => _history.Record(location);

        // The new preview takes the place of the old one.
        var old = preview ? Documents.FirstOrDefault(d => d.IsPreview) : null;
        if (old is not null)
        {
            var at = Documents.IndexOf(old);
            Documents.RemoveAt(at);
            old.OnClosed();
            Documents.Insert(at, document);
        }
        else
        {
            Documents.Add(document);
        }

        ActiveDocument = document;
        _ = document.LoadAsync(_loader, _output, CancellationToken.None);
    }

    /// <summary>
    /// Reads a file from disk again, for a log that was still being written when it was collected, or a file that has
    /// since changed. Findings and the timeline keep what they found in the file the first time.
    /// </summary>
    [RelayCommand]
    private Task Refresh(DocumentViewModel? document)
    {
        if ((document ?? ActiveDocument) is not ArtifactDocumentViewModel artifact)
        {
            return Task.CompletedTask;
        }

        _cache?.Invalidate(artifact.Artifact.Id);
        return artifact.ReloadAsync(_loader, _output, CancellationToken.None);
    }

    /// <summary>Keeps a preview tab: it stays when the next file is previewed.</summary>
    [RelayCommand]
    private void Pin(DocumentViewModel? document)
    {
        if (document is not null)
        {
            document.IsPreview = false;
        }
    }

    /// <summary>Opens whatever <paramref name="location"/> points at. Viewers will scroll/select once they exist.</summary>
    public bool OpenLocation(DiagnosticLocation location, SearchHighlight? highlight = null)
    {
        if (location.Kind == DiagnosticLocationKind.Timeline)
        {
            return ShowTimeline() is not null;
        }

        var artifact = _workspace.Current?.Find(location.ArtifactId);
        if (artifact is null)
        {
            _output.Write(OutputSeverity.Warning, "Navigation", "Location not found in this investigation: " + location);
            return false;
        }

        // Showing the tab records the artifact on its own. When the target is a place inside it (a line, an event), that
        // place is recorded instead, so Back leaves it in one step rather than first landing on the artifact.
        var specific = location.Kind != DiagnosticLocationKind.Artifact;
        var wasNavigating = _isNavigating;
        _isNavigating = _isNavigating || specific;
        try
        {
            OpenArtifact(artifact);
        }
        finally
        {
            _isNavigating = wasNavigating;
        }

        if (specific)
        {
            if (Documents.OfType<ArtifactDocumentViewModel>().FirstOrDefault(d => d.Artifact.Id == artifact.Id) is { } document)
            {
                document.NavigateTo(location, highlight);
            }

            if (!_isNavigating)
            {
                _history.Record(location);
            }
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
        document.OnClosed();
        if (ReferenceEquals(ActiveDocument, document) || ActiveDocument is null)
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        }
    }

    [RelayCommand]
    private void CloseAll()
    {
        foreach (var document in Documents.Where(d => d.CanClose).ToList())
        {
            Documents.Remove(document);
            document.OnClosed();
        }

        ActiveDocument = Documents.FirstOrDefault();
    }

    [RelayCommand]
    private void CloseOthers(DocumentViewModel? keep)
    {
        keep ??= ActiveDocument;
        foreach (var document in Documents.Where(d => d.CanClose && !ReferenceEquals(d, keep)).ToList())
        {
            Documents.Remove(document);
            document.OnClosed();
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
        Timeline?.Cancel();
        Timeline = null;
        foreach (var document in Documents)
        {
            document.OnClosed();
        }

        Documents.Clear();
        ActiveDocument = null;
        _history.Clear();
        ShowOverview();
    }
}
