using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.App.ViewModels.EventLogViewer;

/// <param name="MaxLevel">Keep events at this level or more severe (lower numbers); <c>null</c> keeps every level.</param>
public sealed record LevelOption(string Label, byte? MaxLevel);

/// <param name="Id"><c>null</c> means every provider.</param>
public sealed record ProviderOption(ushort? Id, string Label);

/// <summary>Event Viewer–style table with filters, an event detail pane and raw XML for an offline event log.</summary>
public sealed partial class EventLogViewerViewModel : ObservableObject, ILocationNavigable, ICurrentPosition
{
    public const int DetailsTab = 0;
    public const int XmlTab = 1;

    private const int FilterDebounceMilliseconds = 250;

    public static IReadOnlyList<LevelOption> LevelOptions { get; } = new[]
    {
        new LevelOption("All levels", null),
        new LevelOption("Critical", EventLevels.Critical),
        new LevelOption("Error or worse", EventLevels.Error),
        new LevelOption("Warning or worse", EventLevels.Warning),
        new LevelOption("Information or worse", EventLevels.Information),
    };

    private static readonly ProviderOption AllProviders = new(null, "All providers");

    public DiagnosticLocation? CurrentPosition(Guid artifactId) =>
        SelectedEvent is { } selected ? DiagnosticLocation.ForEventRecord(artifactId, selected.RecordId) : null;

    private CancellationTokenSource? _filterCts;
    private bool _suppressFilter;

    /// <param name="reloaded">
    /// The file was just read again. "Not cleanly closed" is stored in the file itself, so reading the same file again
    /// cannot clear it; the banner then says so and stops offering a refresh that changes nothing.
    /// </param>
    public EventLogViewerViewModel(EventLogDocument document, bool reloaded = false)
    {
        Document = document;
        Source = document.Source;
        _selectedLevel = LevelOptions[0];
        _selectedProvider = AllProviders;
        _events = new VirtualEventList(Source, null, newestFirst: false);

        ProviderOptions = new[] { AllProviders }
            .Concat(Source.Providers.Select(p => new ProviderOption(p.Id, $"{p.Name} ({p.Count:N0})")))
            .ToList();

        WarningText = BuildWarning(document, reloaded);
        RefreshOffered = !(reloaded && document.IsDirty);
        UpdateStatus(filtered: false, filteredCount: Source.Count);
    }

    public EventLogDocument Document { get; }
    public IEventLogSource Source { get; }
    public IReadOnlyList<ProviderOption> ProviderOptions { get; }

    /// <summary>Banner text for unclean logs and parse problems; <c>null</c> for a clean file.</summary>
    public string? WarningText { get; }

    /// <summary>Whether the banner offers to read the file again.</summary>
    public bool RefreshOffered { get; }

    /// <summary>Raised when the view should scroll a row (index into <see cref="Events"/>) into view.</summary>
    public event EventHandler<int>? ScrollToRowRequested;

    [ObservableProperty]
    private VirtualEventList _events;

    [ObservableProperty]
    private EventRowViewModel? _selectedEvent;

    [ObservableProperty]
    private EventDetailViewModel? _selectedDetail;

    [ObservableProperty]
    private int _selectedTabIndex = DetailsTab;

    [ObservableProperty]
    private LevelOption _selectedLevel;

    [ObservableProperty]
    private ProviderOption _selectedProvider;

    [ObservableProperty]
    private string _eventIdText = string.Empty;

    [ObservableProperty]
    private string? _eventIdError;

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    /// <summary>Also search message text. Opt-in because rendering every event's message is much slower.</summary>
    [ObservableProperty]
    private bool _includeMessage;

    [ObservableProperty]
    private bool _newestFirst;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isFiltering;

    public bool HasActiveFilter => CurrentCriteria(out _).IsEmpty is false;

    /// <summary>The in-flight or most recent filter run; lets callers and tests await completion.</summary>
    public Task PendingFilter { get; private set; } = Task.CompletedTask;

    // ---- filter plumbing ----

    partial void OnSelectedLevelChanged(LevelOption value) => RestartFilter(debounce: false);

    partial void OnSelectedProviderChanged(ProviderOption value) => RestartFilter(debounce: false);

    partial void OnEventIdTextChanged(string value) => RestartFilter(debounce: true);

    partial void OnFindTextChanged(string value) => RestartFilter(debounce: true);

    partial void OnMatchCaseChanged(bool value)
    {
        if (!string.IsNullOrEmpty(FindText))
        {
            RestartFilter(debounce: false);
        }
    }

    partial void OnIncludeMessageChanged(bool value)
    {
        if (!string.IsNullOrEmpty(FindText))
        {
            RestartFilter(debounce: false);
        }
    }

    partial void OnNewestFirstChanged(bool value) => RestartFilter(debounce: false);

    partial void OnSelectedEventChanged(EventRowViewModel? value) =>
        SelectedDetail = value is null ? null : new EventDetailViewModel(Source.ReadDetail(value.EventIndex));

    private EventFilterCriteria CurrentCriteria(out string? idError)
    {
        idError = null;
        EventIdSet.TryParse(EventIdText, out var ids, out idError);

        return new EventFilterCriteria
        {
            Levels = SelectedLevel.MaxLevel is { } max
                ? Enumerable.Range(0, max + 1).Select(l => (byte)l).ToHashSet()
                : null,
            ProviderIds = SelectedProvider.Id is { } id ? new HashSet<ushort> { id } : null,
            EventIds = ids,
            Text = string.IsNullOrEmpty(FindText) ? null : FindText,
            MatchCase = MatchCase,
            IncludeMessage = IncludeMessage,
        };
    }

    private void RestartFilter(bool debounce)
    {
        if (_suppressFilter)
        {
            return;
        }

        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        PendingFilter = RunFilterAsync(debounce, _filterCts.Token);
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    private async Task RunFilterAsync(bool debounce, CancellationToken token)
    {
        try
        {
            var criteria = CurrentCriteria(out var idError);
            EventIdError = idError;

            if (debounce)
            {
                await Task.Delay(FilterDebounceMilliseconds, token).ConfigureAwait(true);
            }

            int[]? view = null;
            if (!criteria.IsEmpty)
            {
                IsFiltering = true;
                StatusText = "Filtering...";
                view = await EventLogFilter.ApplyAsync(Source, criteria, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
            }

            ShowView(view, view?.Length ?? Source.Count);
        }
        catch (OperationCanceledException)
        {
            // A newer filter run owns the state now.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsFiltering = false;
            }
        }
    }

    private void ShowView(int[]? view, int shown)
    {
        var keep = SelectedEvent?.RecordId;
        Events = new VirtualEventList(Source, view, NewestFirst);
        UpdateStatus(view is not null, shown);

        SelectedEvent = null;
        if (keep is { } recordId && Source.FindByRecordId(recordId) is var index and >= 0
            && Events.RowOfEvent(index) is var row and >= 0)
        {
            SelectedEvent = Events[row];
            ScrollToRowRequested?.Invoke(this, row);
        }
    }

    private void UpdateStatus(bool filtered, int filteredCount) =>
        StatusText = filtered
            ? string.Create(CultureInfo.CurrentCulture, $"{filteredCount:N0} of {Source.Count:N0} events")
            : string.Create(CultureInfo.CurrentCulture, $"{Source.Count:N0} events");

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilter = true;
        try
        {
            SelectedLevel = LevelOptions[0];
            SelectedProvider = AllProviders;
            EventIdText = string.Empty;
            FindText = string.Empty;
        }
        finally
        {
            _suppressFilter = false;
        }

        _filterCts?.Cancel();
        EventIdError = null;
        IsFiltering = false;
        OnPropertyChanged(nameof(HasActiveFilter));
        ShowView(null, Source.Count);
    }

    // ---- navigation ----

    public bool NavigateTo(DiagnosticLocation location)
    {
        if (location.Kind != DiagnosticLocationKind.EventRecord || location.NumericPosition is not { } recordId)
        {
            return false;
        }

        var index = Source.FindByRecordId(recordId);
        if (index < 0)
        {
            return false;
        }

        var row = Events.RowOfEvent(index);
        if (row < 0)
        {
            // Filtered out: show everything so the evidence is visible.
            ClearFilters();
            row = Events.RowOfEvent(index);
        }

        if (row < 0)
        {
            return false;
        }

        SelectedTabIndex = DetailsTab;
        SelectedEvent = Events[row];
        ScrollToRowRequested?.Invoke(this, row);
        return true;
    }

    // ---- banner ----

    private static string? BuildWarning(EventLogDocument document, bool reloaded)
    {
        var parts = new List<string>();
        if (document.IsDirty)
        {
            parts.Add(reloaded
                ? "Read again from disk, and the file itself is still marked \"not cleanly closed\" (the mark is stored in the file, so reading it again cannot clear it). "
                    + "It may have been copied while in use, so recent events could be missing. Collect the log again to get a closed copy."
                : "This log was not cleanly closed (it may have been copied while in use), so recent events could be missing.");
        }

        if (document.TotalIssueCount > 0)
        {
            var first = document.Issues.Count > 0 ? " First: " + document.Issues[0].Message : string.Empty;
            parts.Add($"{document.TotalIssueCount:N0} problems were found while reading the file; affected events may be missing or incomplete.{first}");
        }

        parts.AddRange(document.Notes);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
