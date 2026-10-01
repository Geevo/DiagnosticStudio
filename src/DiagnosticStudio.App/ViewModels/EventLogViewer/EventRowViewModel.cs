using System.Globalization;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.EventLogViewer;

/// <summary>
/// One row of the event table. Index-level facts are available immediately; the message preview decodes the
/// event the first time a row is actually displayed, so scrolling past rows costs nothing.
/// </summary>
public sealed class EventRowViewModel
{
    private const int PreviewLength = 400;

    private readonly IEventLogSource _source;
    private string? _preview;
    private bool? _hasMessage;

    public EventRowViewModel(IEventLogSource source, EventSummary summary)
    {
        _source = source;
        Summary = summary;
    }

    public EventSummary Summary { get; }

    public int EventIndex => Summary.Index;
    public long RecordId => Summary.RecordId;
    public uint EventId => Summary.EventId;
    public byte Level => Summary.Level;
    public string LevelName => EventLevels.Name(Summary.Level);

    /// <summary>Used by the grid to colour rows: <c>Critical</c>, <c>Error</c>, <c>Warning</c> or empty.</summary>
    public string Severity => Summary.Level switch
    {
        EventLevels.Critical => "Critical",
        EventLevels.Error => "Error",
        EventLevels.Warning => "Warning",
        _ => string.Empty,
    };

    public string Provider => _source.ProviderName(Summary.ProviderId);

    public string TimeText => Summary.TimeUtc == DateTime.MinValue
        ? string.Empty
        : Summary.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>First line of the message, or a summary of the event data when no message text is available here.</summary>
    public string MessagePreview
    {
        get
        {
            Load();
            return _preview!;
        }
    }

    /// <summary>False when the preview is a data summary rather than a formatted message.</summary>
    public bool HasMessage
    {
        get
        {
            Load();
            return _hasMessage!.Value;
        }
    }

    private void Load()
    {
        if (_preview is not null)
        {
            return;
        }

        var detail = _source.ReadDetail(Summary.Index);
        if (detail.DecodeError is not null)
        {
            _preview = "(record could not be decoded)";
            _hasMessage = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(detail.Message))
        {
            _preview = FirstLine(detail.Message);
            _hasMessage = true;
            return;
        }

        _preview = detail.Data.Count == 0
            ? string.Empty
            : Truncate(string.Join(", ", detail.Data.Select(d => d.Name is null ? d.Value : d.Name + "=" + d.Value)));
        _hasMessage = false;
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return Truncate(end >= 0 ? text[..end] : text);
    }

    private static string Truncate(string text) =>
        text.Length <= PreviewLength ? text : text[..PreviewLength] + "…";
}
