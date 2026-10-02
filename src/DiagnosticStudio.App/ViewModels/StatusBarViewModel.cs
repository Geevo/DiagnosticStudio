using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.App.ViewModels.Search;
using DiagnosticStudio.App.ViewModels.Timeline;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>
/// What the status bar says: "Ready", or what the application is busy with (opening an archive, loading files,
/// evaluating rules, building the timeline, searching), so a pause is never unexplained.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    public const string Idle = "Ready";

    private readonly WorkspaceViewModel _workspace;
    private readonly DocumentHostViewModel _documents;
    private readonly ProblemsViewModel _problems;
    private readonly SearchResultsViewModel _search;
    private readonly HashSet<INotifyPropertyChanged> _watched = new();

    public StatusBarViewModel(
        WorkspaceViewModel workspace,
        DocumentHostViewModel documents,
        ProblemsViewModel problems,
        SearchResultsViewModel search)
    {
        _workspace = workspace;
        _documents = documents;
        _problems = problems;
        _search = search;
        _text = Idle;

        Watch(workspace);
        Watch(problems);
        Watch(search);
        Watch(documents);
        documents.Documents.CollectionChanged += OnDocumentsChanged;
        WatchDocuments();
        Refresh();
    }

    /// <summary>The status line.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>Something is running; the bar shows a progress indicator.</summary>
    [ObservableProperty]
    private bool _isBusy;

    private void Watch(INotifyPropertyChanged source)
    {
        if (_watched.Add(source))
        {
            source.PropertyChanged += OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _documents) && e.PropertyName == nameof(DocumentHostViewModel.Timeline))
        {
            WatchDocuments();
        }

        Refresh();
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchDocuments();
        Refresh();
    }

    // Tabs come and go; each one that can be busy is watched once.
    private void WatchDocuments()
    {
        foreach (var document in _documents.Documents)
        {
            if (document is ArtifactDocumentViewModel artifact)
            {
                Watch(artifact);
            }
        }

        if (_documents.Timeline is { } timeline)
        {
            Watch(timeline);
        }
    }

    /// <summary>Recomputes the line. Called whenever something that can be busy changes.</summary>
    public void Refresh()
    {
        var busy = Describe();
        IsBusy = busy.Count > 0;
        Text = busy.Count == 0 ? Idle : string.Join("  ·  ", busy);
    }

    private List<string> Describe()
    {
        var culture = CultureInfo.CurrentCulture;
        var parts = new List<string>();

        if (_workspace.IsLoading)
        {
            // Opening replaces everything else that was going on.
            var text = "Opening archive";
            if (!string.IsNullOrWhiteSpace(_workspace.ProgressMessage))
            {
                text += ": " + _workspace.ProgressMessage.TrimEnd('.');
            }

            if (!string.IsNullOrWhiteSpace(_workspace.ProgressDetail))
            {
                text += " (" + _workspace.ProgressDetail + ")";
            }

            parts.Add(text + "...");
            return parts;
        }

        var loading = _documents.Documents.OfType<ArtifactDocumentViewModel>().Where(d => d.IsLoading).ToList();
        if (loading.Count == 1)
        {
            parts.Add("Loading " + loading[0].Title + "...");
        }
        else if (loading.Count > 1)
        {
            parts.Add(string.Create(culture, $"Loading {loading.Count:N0} files..."));
        }

        if (_problems.IsEvaluating)
        {
            parts.Add(_problems.StatusText);
        }

        if (_documents.Timeline is { IsBuilding: true } timeline)
        {
            parts.Add(string.IsNullOrEmpty(timeline.ProgressText) ? "Building timeline..." : "Building timeline: " + timeline.ProgressText);
        }

        if (_search.IsSearching)
        {
            parts.Add(string.Create(culture, $"Searching {_search.ArtifactsSearched:N0} of {_search.ArtifactsTotal:N0} files..."));
        }

        return parts;
    }
}
