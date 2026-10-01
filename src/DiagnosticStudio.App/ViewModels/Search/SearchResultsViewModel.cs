using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.App.ViewModels.Search;

/// <summary>One matching place in an artifact, ready to display and to navigate to.</summary>
public sealed partial class SearchHitViewModel : ObservableObject
{
    public SearchHitViewModel(SearchHit hit)
    {
        Hit = hit;
        Location = hit.Location;
        LocationText = hit.Location.Kind switch
        {
            DiagnosticLocationKind.Line => "Line " + hit.Location.NumericPosition?.ToString("N0", CultureInfo.CurrentCulture),
            DiagnosticLocationKind.EventRecord => "Event " + hit.Location.NumericPosition?.ToString(CultureInfo.CurrentCulture),
            DiagnosticLocationKind.Registry => hit.MatchField is { } field ? "Registry " + field : "Registry",
            _ => hit.MatchField ?? "File",
        };
        Preview = hit.Preview;
        MatchStart = hit.MatchStart;
        MatchLength = hit.MatchLength;
    }

    /// <summary>Placeholder row explaining that more results exist than are listed.</summary>
    public SearchHitViewModel(string message)
    {
        Hit = null;
        Location = null;
        LocationText = string.Empty;
        Preview = message;
        IsPlaceholder = true;
    }

    public SearchHit? Hit { get; }
    public DiagnosticLocation? Location { get; }
    public string LocationText { get; }
    public string Preview { get; }
    public int MatchStart { get; }
    public int MatchLength { get; }
    public bool IsPlaceholder { get; }

    // Present so the tree's container style can bind the same properties on every node kind.
    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>An artifact with hits: its name, where it came from, the hit count and the first hits.</summary>
public sealed partial class SearchArtifactNodeViewModel : ObservableObject
{
    public SearchArtifactNodeViewModel(ArtifactSearchResult result)
    {
        Result = result;
        Artifact = result.Artifact;
        Title = result.Artifact.Name;
        Provenance = result.Artifact.ProvenanceDisplay;
        CountText = result.TotalHits.ToString("N0", CultureInfo.CurrentCulture) + (result.CountIsLowerBound ? "+" : string.Empty);
        TotalHits = result.TotalHits;

        foreach (var hit in result.Hits)
        {
            Hits.Add(new SearchHitViewModel(hit));
        }

        if (result.IsTruncated)
        {
            var hidden = result.TotalHits - result.Hits.Count;
            Hits.Add(new SearchHitViewModel(string.Create(
                CultureInfo.CurrentCulture,
                $"… {hidden:N0}{(result.CountIsLowerBound ? "+" : string.Empty)} more not listed. Open the file and use its find, or refine the query.")));
        }
    }

    public ArtifactSearchResult Result { get; }
    public DiagnosticArtifact Artifact { get; }
    public string Title { get; }
    public string Provenance { get; }
    public string CountText { get; }
    public int TotalHits { get; }
    public ObservableCollection<SearchHitViewModel> Hits { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// Global search over the open bundle. Results stream in as artifacts finish, grouped by artifact and ordered by hit
/// count; activating a hit opens the artifact at the exact location.
/// </summary>
public sealed partial class SearchResultsViewModel : ObservableObject
{
    /// <summary>Hits listed up front before further artifacts start collapsed, to keep the tree manageable.</summary>
    private const int ExpandedHitBudget = 300;

    private readonly WorkspaceViewModel _workspace;
    private readonly IGlobalSearchService _service;
    private readonly DocumentHostViewModel _documents;
    private readonly IOutputLog _output;
    private CancellationTokenSource? _cts;
    private SearchQuery? _lastQuery;
    private int _listedHits;
    private int _runId;

    public SearchResultsViewModel(
        WorkspaceViewModel workspace,
        IGlobalSearchService service,
        DocumentHostViewModel documents,
        IOutputLog output)
    {
        _workspace = workspace;
        _service = service;
        _documents = documents;
        _output = output;
        _workspace.WorkspaceChanged += (_, _) => Reset();
    }

    public ObservableCollection<SearchArtifactNodeViewModel> Results { get; } = new();

    /// <summary>Raised when a search begins, so the shell can bring the results panel forward.</summary>
    public event EventHandler? SearchStarted;

    /// <summary>The in-flight or most recent search; lets callers and tests await completion.</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    /// <summary>Also match event message text. Opt-in because rendering every message is much slower.</summary>
    [ObservableProperty]
    private bool _includeEventMessages;

    [ObservableProperty]
    private bool _includeFileNames = true;

    [ObservableProperty]
    private string _statusText = "Type a query in the search box and press Enter. Operators: eventid:7031  provider:schannel  level:error  type:registry";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private int _artifactsSearched;

    [ObservableProperty]
    private int _artifactsTotal;

    public bool HasResults => Results.Count > 0;

    partial void OnMatchCaseChanged(bool value) => Rerun();

    partial void OnIncludeEventMessagesChanged(bool value) => Rerun();

    partial void OnIncludeFileNamesChanged(bool value) => Rerun();

    // Changing an option re-runs the query that produced the current results (not text typed but not yet submitted).
    private void Rerun()
    {
        if (_lastQuery is not null && !string.IsNullOrWhiteSpace(Query))
        {
            SearchCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void Search()
    {
        _cts?.Cancel();
        QueryError = null;

        var parsed = SearchQueryParser.Parse(Query, MatchCase);
        if (parsed.Errors.Count > 0)
        {
            QueryError = parsed.Errors[0];
            return;
        }

        if (parsed.Query.IsEmpty)
        {
            Reset();
            return;
        }

        if (_workspace.Current is not { } workspace)
        {
            StatusText = "Open a bundle before searching.";
            return;
        }

        _cts = new CancellationTokenSource();
        var run = ++_runId;
        SearchStarted?.Invoke(this, EventArgs.Empty);
        PendingSearch = RunAsync(run, workspace.Artifacts, parsed.Query, _cts.Token);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(int run, IReadOnlyList<DiagnosticArtifact> artifacts, SearchQuery query, CancellationToken token)
    {
        _lastQuery = query;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        _listedHits = 0;
        IsSearching = true;
        ArtifactsSearched = 0;
        ArtifactsTotal = artifacts.Count;
        StatusText = "Searching...";

        var options = new SearchOptions
        {
            IncludeEventMessages = IncludeEventMessages,
            IncludeFileNames = IncludeFileNames,
        };
        var clock = Stopwatch.StartNew();
        long totalHits = 0;
        string suffix = string.Empty;

        try
        {
            await foreach (var update in _service.SearchAsync(artifacts, query, options, token).ConfigureAwait(true))
            {
                if (run != _runId)
                {
                    return; // superseded by a newer search or a reset
                }

                ArtifactsSearched = update.ArtifactsSearched;
                ArtifactsTotal = update.ArtifactsTotal;
                totalHits = update.TotalHits;

                if (update.Issue is { } issue)
                {
                    _output.Write(OutputSeverity.Warning, "Search", issue.Message);
                }

                if (update.Result is { } result)
                {
                    Insert(new SearchArtifactNodeViewModel(result));
                }

                StatusText = Summarise(totalHits, searching: true, clock.Elapsed);
            }
        }
        catch (OperationCanceledException)
        {
            suffix = " (cancelled, partial results)";
        }
        catch (Exception ex)
        {
            if (run == _runId)
            {
                _output.Write(OutputSeverity.Error, "Search", "Search failed: " + ex.Message);
                StatusText = "Search failed: " + ex.Message;
                IsSearching = false;
            }

            return;
        }

        // Only the newest search reports its outcome.
        if (run == _runId)
        {
            IsSearching = false;
            StatusText = Summarise(totalHits, searching: false, clock.Elapsed) + suffix;
        }
    }

    private string Summarise(long totalHits, bool searching, TimeSpan elapsed)
    {
        var culture = CultureInfo.CurrentCulture;
        if (!searching && totalHits == 0)
        {
            return string.Create(culture, $"No results. Searched {ArtifactsSearched:N0} artifacts in {elapsed.TotalSeconds:0.0} s.");
        }

        var head = string.Create(culture, $"{totalHits:N0} results in {Results.Count:N0} artifacts");
        return searching
            ? string.Create(culture, $"{head} · searched {ArtifactsSearched:N0} of {ArtifactsTotal:N0} artifacts...")
            : string.Create(culture, $"{head} · searched {ArtifactsSearched:N0} artifacts in {elapsed.TotalSeconds:0.0} s");
    }

    // Keeps artifacts ordered by hit count (then name) as they arrive in completion order.
    private void Insert(SearchArtifactNodeViewModel node)
    {
        var index = 0;
        while (index < Results.Count
               && (Results[index].TotalHits > node.TotalHits
                   || (Results[index].TotalHits == node.TotalHits
                       && string.Compare(Results[index].Title, node.Title, StringComparison.OrdinalIgnoreCase) <= 0)))
        {
            index++;
        }

        _listedHits += node.Hits.Count;
        // The first artifact always opens so a lone result is immediately visible; later ones stay within the budget.
        node.IsExpanded = Results.Count == 0 || _listedHits <= ExpandedHitBudget;
        Results.Insert(index, node);
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Opens the evidence behind a hit and, where the viewer supports it, highlights the query there.</summary>
    [RelayCommand]
    private void OpenHit(SearchHitViewModel? hit)
    {
        if (hit?.Location is not { } location)
        {
            return;
        }

        var query = _lastQuery;
        var highlight = query is { Text.Length: > 0 } ? new SearchHighlight(query.Text, query.MatchCase) : null;
        _documents.OpenLocation(location, highlight);
    }

    /// <summary>Shows when the place a hit points at happened, among everything else in the bundle.</summary>
    [RelayCommand(CanExecute = nameof(CanShowInTimeline))]
    private Task ShowHitInTimeline(object? item) =>
        item is SearchHitViewModel { Location: { } location } ? _documents.ShowInTimelineAsync(location) : Task.CompletedTask;

    // Takes any row of the results tree (artifact rows have no single place), so the menu can bind every row.
    private static bool CanShowInTimeline(object? item) => item is SearchHitViewModel { Location: not null };

    [RelayCommand]
    private void OpenArtifact(SearchArtifactNodeViewModel? node)
    {
        if (node is not null)
        {
            _documents.OpenArtifact(node.Artifact);
        }
    }

    private void Reset()
    {
        _runId++;
        _cts?.Cancel();
        _lastQuery = null;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        IsSearching = false;
        ArtifactsSearched = 0;
        ArtifactsTotal = 0;
        StatusText = "Type a query in the search box and press Enter. Operators: eventid:7031  provider:schannel  level:error  type:registry";
    }
}
