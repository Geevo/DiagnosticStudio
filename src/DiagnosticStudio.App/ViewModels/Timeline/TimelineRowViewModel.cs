using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Timeline;

namespace DiagnosticStudio.App.ViewModels.Timeline;

/// <summary>
/// One row of the timeline. The time, level and source are known immediately; the text is read from the source file
/// the first time the row is displayed, so scrolling past rows costs nothing.
/// </summary>
public sealed class TimelineRowViewModel : ObservableObject
{
    private readonly ITimelineService _service;
    private readonly TimelineIndex _index;
    private readonly TimeSpan[]? _offsets;
    private string? _text;
    private Task? _loading;

    public TimelineRowViewModel(
        TimelineIndex index,
        int entryIndex,
        int rowNumber,
        ITimelineService service,
        TimeSpan[]? offsets,
        object owner)
    {
        _index = index;
        _service = service;
        _offsets = offsets;
        EntryIndex = entryIndex;
        RowNumber = rowNumber;
        Owner = owner;
    }

    /// <summary>The list this row belongs to; a row of a replaced list is not selectable in the new one.</summary>
    public object Owner { get; }

    public int EntryIndex { get; }
    public int RowNumber { get; }

    public TimelineEntry Entry => _index.Entries[EntryIndex];
    public TimelineSource Source => _index.Sources[Entry.Source];

    public DiagnosticLocation Location => _index.LocationOf(Entry);

    /// <summary>UTC, with a leading <c>~</c> when the time was written without a zone and is therefore an assumption.</summary>
    public string TimeText
    {
        get
        {
            var entry = Entry;
            var utc = new DateTime(TimelineSelector.EffectiveTicks(entry, _offsets), DateTimeKind.Utc);
            return (entry.Unzoned ? "~" : string.Empty) + utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }

    public DateTime TimeUtc => new(TimelineSelector.EffectiveTicks(Entry, _offsets), DateTimeKind.Utc);

    public bool IsUnzoned => Entry.Unzoned;

    public string LevelName => Entry.Severity switch
    {
        LogSeverity.Error => "Error",
        LogSeverity.Warning => "Warning",
        LogSeverity.Information => "Information",
        LogSeverity.Debug => "Debug",
        _ => string.Empty,
    };

    /// <summary>Used by the grid to colour rows: <c>Error</c>, <c>Warning</c> or empty.</summary>
    public string Severity => Entry.Severity is LogSeverity.Error or LogSeverity.Warning ? LevelName : string.Empty;

    public string SourceName => Source.Artifact.Name;

    public string SourceToolTip => Source.Artifact.ProvenanceDisplay;

    public string PositionText => Source.Kind == TimelineSourceKind.EventLog
        ? "event " + Entry.Position.ToString(CultureInfo.InvariantCulture)
        : "line " + Entry.Position.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The text of the log line or event; a placeholder until it has been read.</summary>
    public string Text
    {
        get
        {
            if (_text is null && _loading is null)
            {
                _loading = LoadAsync();
            }

            return _text ?? string.Empty;
        }
    }

    /// <summary>Completes when <see cref="Text"/> has been read. Lets callers and tests wait for it.</summary>
    public Task Loaded => _loading ?? (_loading = LoadAsync());

    private async Task LoadAsync()
    {
        string text;
        try
        {
            text = await _service.DescribeAsync(_index, Entry, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            text = "(could not be read: " + ex.Message + ")";
        }

        _text = text;
        OnPropertyChanged(nameof(Text));
    }
}
