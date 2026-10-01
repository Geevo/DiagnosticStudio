using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Timeline;

namespace DiagnosticStudio.App.ViewModels.Timeline;

/// <summary>A zone a source's unzoned times can be assumed to be written in.</summary>
public sealed record UtcOffsetOption(string Label, TimeSpan Offset)
{
    public static UtcOffsetOption Utc { get; } = new("UTC", TimeSpan.Zero);

    /// <summary>UTC-12:00 to UTC+14:00 in half-hour steps (zones with 45-minute offsets are not offered).</summary>
    public static IReadOnlyList<UtcOffsetOption> All { get; } = Build();

    private static List<UtcOffsetOption> Build()
    {
        var list = new List<UtcOffsetOption>();
        for (var minutes = -12 * 60; minutes <= 14 * 60; minutes += 30)
        {
            if (minutes == 0)
            {
                list.Add(Utc);
                continue;
            }

            var span = TimeSpan.FromMinutes(minutes);
            var sign = minutes < 0 ? "-" : "+";
            list.Add(new UtcOffsetOption($"UTC{sign}{Math.Abs(span.Hours):00}:{Math.Abs(span.Minutes):00}", span));
        }

        return list;
    }
}

public sealed record AroundOption(string Label, TimeSpan Window)
{
    public static IReadOnlyList<AroundOption> All { get; } = new AroundOption[]
    {
        new("±10 seconds", TimeSpan.FromSeconds(10)),
        new("±1 minute", TimeSpan.FromMinutes(1)),
        new("±5 minutes", TimeSpan.FromMinutes(5)),
        new("±1 hour", TimeSpan.FromHours(1)),
    };
}

public sealed record SeverityOption(string Label, TimelineSeverityFilter Filter)
{
    public static IReadOnlyList<SeverityOption> All { get; } = new SeverityOption[]
    {
        new("All levels", TimelineSeverityFilter.All),
        new("Hide debug", TimelineSeverityFilter.HideDebug),
        new("Warnings and errors", TimelineSeverityFilter.WarningsAndErrors),
        new("Errors only", TimelineSeverityFilter.ErrorsOnly),
    };
}

/// <summary>One log or event log in the timeline's source list: whether it is shown, and for unzoned logs, its zone.</summary>
public sealed partial class TimelineSourceViewModel : ObservableObject
{
    private readonly Action _changed;

    public TimelineSourceViewModel(TimelineSource source, Action changed)
    {
        Source = source;
        _changed = changed;
    }

    public TimelineSource Source { get; }

    public string Name => Source.Artifact.Name;
    public string ToolTip => Source.Artifact.ProvenanceDisplay;
    public string CountText => Source.EntryCount.ToString("N0", CultureInfo.CurrentCulture);
    public bool HasUnzonedTimes => Source.HasUnzonedTimes;

    public IReadOnlyList<UtcOffsetOption> OffsetOptions => UtcOffsetOption.All;

    [ObservableProperty]
    private bool _isIncluded = true;

    [ObservableProperty]
    private UtcOffsetOption _offset = UtcOffsetOption.Utc;

    partial void OnIsIncludedChanged(bool value) => _changed();

    partial void OnOffsetChanged(UtcOffsetOption value) => _changed();
}

/// <summary>
/// One time-ordered list of everything timestamped in the bundle's event logs and text logs. Built in the background
/// when the tab is first opened; every row links to its source through <see cref="DiagnosticLocation"/>.
/// </summary>
public sealed partial class TimelineDocumentViewModel : DocumentViewModel
{
    private readonly IReadOnlyList<DiagnosticArtifact> _artifacts;
    private readonly ITimelineService _service;
    private readonly Action<DiagnosticLocation> _open;
    private readonly IOutputLog _output;
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _selectCts;
    private TimelineIndex _index = TimelineIndex.Empty;
    private int _run;
    private bool _batching;
    private TimeSpan[]? _offsets;

    public TimelineDocumentViewModel(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        ITimelineService service,
        Action<DiagnosticLocation> open,
        IOutputLog output)
    {
        _artifacts = artifacts;
        _service = service;
        _open = open;
        _output = output;
        _rows = VirtualTimelineList.Empty;
        _statusText = "Reading logs and event logs...";
        _isBuilding = true;
        PendingBuild = BuildAsync();
    }

    public override string Title => "Timeline";
    public override string? ToolTip => "Everything with a timestamp in the bundle's event logs and text logs, in time order";

    public IReadOnlyList<SeverityOption> SeverityOptions => SeverityOption.All;
    public IReadOnlyList<AroundOption> AroundOptions => AroundOption.All;

    public ObservableCollection<TimelineSourceViewModel> Sources { get; } = new();

    /// <summary>The build in progress or finished; lets callers and tests await it.</summary>
    public Task PendingBuild { get; }

    /// <summary>The most recent re-ordering or re-filtering.</summary>
    public Task PendingSelection { get; private set; } = Task.CompletedTask;

    public TimelineIndex Index => _index;

    /// <summary>Raised with a row number when the view should scroll that row into view.</summary>
    public event EventHandler<int>? ScrollRequested;

    [ObservableProperty]
    private VirtualTimelineList _rows;

    [ObservableProperty]
    private TimelineRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private bool _isFiltering;

    [ObservableProperty]
    private string _notices = string.Empty;

    [ObservableProperty]
    private SeverityOption _selectedSeverity = SeverityOption.All[0];

    [ObservableProperty]
    private string _fromText = string.Empty;

    [ObservableProperty]
    private string _toText = string.Empty;

    [ObservableProperty]
    private string _rangeError = string.Empty;

    [ObservableProperty]
    private AroundOption _selectedAround = AroundOption.All[1];

    [ObservableProperty]
    private string _jumpText = string.Empty;

    public bool HasNotices => Notices.Length > 0;

    public bool HasSources => Sources.Count > 0;

    /// <summary>Stops the build and any filtering; called when the bundle is closed.</summary>
    public void Cancel()
    {
        _lifetime.Cancel();
        _selectCts?.Cancel();
    }

    // ---- building ----

    private async Task BuildAsync()
    {
        var token = _lifetime.Token;
        var progress = new Progress<TimelineProgress>(p =>
        {
            if (IsBuilding)
            {
                ProgressText = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{p.ArtifactsDone:N0} of {p.ArtifactsTotal:N0} logs read, {p.EntriesSoFar:N0} entries so far");
            }
        });

        try
        {
            _index = await Task.Run(() => _service.BuildAsync(_artifacts, progress, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _output.Write(OutputSeverity.Error, "Timeline", "The timeline could not be built: " + ex.Message);
            StatusText = "The timeline could not be built: " + ex.Message;
            IsBuilding = false;
            return;
        }

        foreach (var issue in _index.Issues)
        {
            _output.Write(OutputSeverity.Warning, "Timeline", issue);
        }

        foreach (var source in _index.Sources)
        {
            Sources.Add(new TimelineSourceViewModel(source, OnFilterChanged));
        }

        OnPropertyChanged(nameof(HasSources));
        Notices = BuildNotices(_index);
        IsBuilding = false;
        ProgressText = string.Empty;
        await RefreshAsync().ConfigureAwait(true);
    }

    private static string BuildNotices(TimelineIndex index)
    {
        var culture = CultureInfo.CurrentCulture;
        var notes = new List<string>();

        if (index.Sources.Any(s => s.HasUnzonedTimes))
        {
            notes.Add("Times marked ~ were written without a time zone. They are shown as written, as if UTC, until you set the zone of their source on the left.");
        }

        if (index.IsTruncated)
        {
            notes.Add(string.Create(
                culture,
                $"Only the first {index.Count:N0} entries were kept; {index.DroppedEntries:N0} entries from the last logs are not on the timeline."));
        }

        var untimed = index.ArtifactsExamined - index.Sources.Count;
        if (untimed > 0)
        {
            notes.Add(string.Create(culture, $"{untimed:N0} of {index.ArtifactsExamined:N0} logs had no timestamps that could be read and are not on the timeline."));
        }

        if (index.SkippedCount > 0)
        {
            notes.Add(string.Create(culture, $"{index.SkippedCount:N0} lines without a timestamp (continuation lines, free text) are not listed; open the log to see them."));
        }

        return string.Join(Environment.NewLine, notes);
    }

    partial void OnNoticesChanged(string value) => OnPropertyChanged(nameof(HasNotices));

    // ---- filtering ----

    private void OnFilterChanged()
    {
        if (!_batching)
        {
            PendingSelection = RefreshAsync();
        }
    }

    partial void OnSelectedSeverityChanged(SeverityOption value) => OnFilterChanged();

    partial void OnFromTextChanged(string value) => OnFilterChanged();

    partial void OnToTextChanged(string value) => OnFilterChanged();

    private static bool TryParseUtc(string text, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateTime.TryParse(
                text.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private bool TryBuildFilter(out TimelineFilter filter)
    {
        filter = new TimelineFilter();
        var fromOk = TryParseUtc(FromText, out var from);
        var toOk = TryParseUtc(ToText, out var to);
        if (!fromOk || !toOk)
        {
            RangeError = "Not a date and time (UTC), e.g. 2026-07-23 14:12:00";
            return false;
        }

        if (from is { } f && to is { } t && f > t)
        {
            RangeError = "The start is after the end.";
            return false;
        }

        RangeError = string.Empty;
        _offsets = Sources.Count == 0 ? null : Sources.Select(s => s.Offset.Offset).ToArray();
        filter = new TimelineFilter
        {
            IncludedSources = Sources.Select(s => s.IsIncluded).ToArray(),
            Severity = SelectedSeverity.Filter,
            FromUtc = from,
            ToUtc = to,
            SourceOffsets = _offsets,
        };
        return true;
    }

    private async Task RefreshAsync()
    {
        if (_index.Count == 0)
        {
            Rows = VirtualTimelineList.Empty;
            StatusText = _index.ArtifactsExamined == 0
                ? "This bundle has no event logs or text logs."
                : "No timestamps could be read from this bundle's event logs and text logs.";
            return;
        }

        if (!TryBuildFilter(out var filter))
        {
            return;
        }

        _selectCts?.Cancel();
        _selectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _selectCts.Token;
        var run = ++_run;
        var keep = SelectedRow?.EntryIndex;
        IsFiltering = true;

        try
        {
            var index = _index;
            var order = await Task.Run(() => TimelineSelector.Select(index, filter, token), token).ConfigureAwait(true);
            if (run != _run)
            {
                return;
            }

            SelectedRow = null;
            Rows = new VirtualTimelineList(_index, order, _service, _offsets);
            UpdateStatus();

            if (keep is { } entry && Rows.RowOfEntry(entry) is var row and >= 0)
            {
                SelectRow(row);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer filter owns the list now.
        }
        finally
        {
            if (run == _run)
            {
                IsFiltering = false;
            }
        }
    }

    private void UpdateStatus()
    {
        var culture = CultureInfo.CurrentCulture;
        var shown = Rows.Count;
        var text = string.Create(culture, $"{shown:N0} of {_index.Count:N0} entries from {Sources.Count:N0} logs");
        if (shown > 0)
        {
            var first = new DateTime(TimelineSelector.EffectiveTicks(_index.Entries[Rows.Order[0]], _offsets), DateTimeKind.Utc);
            var last = new DateTime(TimelineSelector.EffectiveTicks(_index.Entries[Rows.Order[^1]], _offsets), DateTimeKind.Utc);
            text += $" · {first:yyyy-MM-dd HH:mm:ss} to {last:yyyy-MM-dd HH:mm:ss} UTC";
        }

        StatusText = text;
    }

    private void SelectRow(int row)
    {
        SelectedRow = Rows[row];
        ScrollRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _batching = true;
        try
        {
            foreach (var source in Sources)
            {
                source.IsIncluded = true;
            }

            SelectedSeverity = SeverityOption.All[0];
            FromText = string.Empty;
            ToText = string.Empty;
        }
        finally
        {
            _batching = false;
        }

        PendingSelection = RefreshAsync();
    }

    [RelayCommand]
    private void ClearRange()
    {
        _batching = true;
        try
        {
            FromText = string.Empty;
            ToText = string.Empty;
        }
        finally
        {
            _batching = false;
        }

        PendingSelection = RefreshAsync();
    }

    /// <summary>Narrows the list to the moments around the selected row, to see what else was happening then.</summary>
    [RelayCommand]
    private void ShowAround()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var time = row.TimeUtc;
        _batching = true;
        try
        {
            FromText = (time - SelectedAround.Window).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            ToText = (time + SelectedAround.Window).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        finally
        {
            _batching = false;
        }

        PendingSelection = RefreshAsync();
    }

    /// <summary>Scrolls to the first entry at or after the time typed in the jump box (UTC).</summary>
    [RelayCommand]
    private void JumpToTime()
    {
        if (!TryParseUtc(JumpText, out var time) || time is null || Rows.Count == 0)
        {
            return;
        }

        var row = TimelineSelector.RowAtOrAfter(_index, Rows.Order, _offsets, time.Value.Ticks);
        if (row >= 0)
        {
            SelectRow(row);
        }
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedRow is { } row)
        {
            _open(row.Location);
        }
    }

    // ---- links in ----

    /// <summary>
    /// Selects the entry for <paramref name="location"/>, clearing filters if they hide it. Returns false when the
    /// place has no timestamp (a line with no time above it, an artifact that is not a log).
    /// </summary>
    public async Task<bool> ShowLocationAsync(DiagnosticLocation location)
    {
        await PendingBuild.ConfigureAwait(true);
        await PendingSelection.ConfigureAwait(true);

        var entry = _index.FindEntry(location);
        if (entry < 0)
        {
            return false;
        }

        var row = Rows.RowOfEntry(entry);
        if (row < 0)
        {
            ClearFilters();
            await PendingSelection.ConfigureAwait(true);
            row = Rows.RowOfEntry(entry);
        }

        if (row < 0)
        {
            return false;
        }

        SelectRow(row);
        return true;
    }
}
