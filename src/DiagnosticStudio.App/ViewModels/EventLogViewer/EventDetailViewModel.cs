using System.Globalization;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.EventLogViewer;

/// <summary>Display model for the selected event: message, system fields, data values and raw XML.</summary>
public sealed class EventDetailViewModel
{
    public const string MessageProvenanceNote =
        "Message text is built from provider metadata registered on this machine and may differ from what the machine that wrote the log would show. The XML is the authoritative record.";

    public const string NoMessageNote =
        "No message text is available for this provider on this machine. The event data is shown instead.";

    public EventDetailViewModel(EventDetail detail)
    {
        Detail = detail;
        Title = string.Create(CultureInfo.InvariantCulture, $"Event {detail.Summary.EventId} · {detail.Provider}");
        Message = detail.Message;
        Note = detail.DecodeError is not null
            ? "This record could not be fully decoded: " + detail.DecodeError
            : detail.Message is null ? NoMessageNote : MessageProvenanceNote;
        Fields = BuildFields(detail);
        Data = detail.Data
            .Select(d => new DetailRow(d.Name ?? "(unnamed)", d.Value))
            .ToList();
        DataTitle = detail.DataSection is null ? "EVENT DATA" : detail.DataSection.ToUpperInvariant();
        Xml = detail.Xml;
    }

    public EventDetail Detail { get; }
    public string Title { get; }
    public string? Message { get; }
    public string Note { get; }
    public IReadOnlyList<DetailRow> Fields { get; }
    public string DataTitle { get; }
    public IReadOnlyList<DetailRow> Data { get; }
    public string Xml { get; }
    public bool HasData => Data.Count > 0;
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    private static IReadOnlyList<DetailRow> BuildFields(EventDetail d)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<DetailRow>
        {
            new("Provider", d.Provider),
            new("Event ID", d.Summary.EventId.ToString(inv)),
            new("Level", $"{EventLevels.Name(d.Summary.Level)} ({d.Summary.Level})"),
            new("Time (UTC)", d.Summary.TimeUtc == DateTime.MinValue
                ? string.Empty
                : d.Summary.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", inv)),
            new("Record ID", d.Summary.RecordId.ToString(inv)),
        };

        Add(rows, "Channel", d.Channel);
        Add(rows, "Computer", d.Computer);
        Add(rows, "User SID", d.UserSid);
        rows.Add(new("Task", d.Task.ToString(inv)));
        rows.Add(new("Opcode", d.Opcode.ToString(inv)));
        rows.Add(new("Keywords", "0x" + d.Keywords.ToString("x16", inv)));
        rows.Add(new("Version", d.Version.ToString(inv)));
        if (d.ProcessId is { } pid)
        {
            rows.Add(new("Process ID", pid.ToString(inv)));
        }

        if (d.ThreadId is { } tid)
        {
            rows.Add(new("Thread ID", tid.ToString(inv)));
        }

        if (d.ProviderGuid is { } guid)
        {
            rows.Add(new("Provider GUID", guid.ToString("B")));
        }

        if (d.ActivityId is { } activity)
        {
            rows.Add(new("Activity ID", activity.ToString("B")));
        }

        if (d.RelatedActivityId is { } related)
        {
            rows.Add(new("Related activity ID", related.ToString("B")));
        }

        return rows;
    }

    private static void Add(List<DetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            rows.Add(new DetailRow(label, value));
        }
    }
}
