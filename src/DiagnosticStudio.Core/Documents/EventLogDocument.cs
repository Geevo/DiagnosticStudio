namespace DiagnosticStudio.Core.Documents;

/// <summary>Windows event levels as stored in the log (0 = log always).</summary>
public static class EventLevels
{
    public const byte LogAlways = 0;
    public const byte Critical = 1;
    public const byte Error = 2;
    public const byte Warning = 3;
    public const byte Information = 4;
    public const byte Verbose = 5;

    public static string Name(byte level) => level switch
    {
        LogAlways => "Log Always",
        Critical => "Critical",
        Error => "Error",
        Warning => "Warning",
        Information => "Information",
        Verbose => "Verbose",
        _ => "Level " + level,
    };
}

public sealed record EventLogProvider(ushort Id, string Name, int Count);

/// <summary>Index-level facts about one event. Cheap to hold for every event in a log.</summary>
/// <param name="Index">Zero-based position in record-id order within the log.</param>
public readonly record struct EventSummary(
    int Index,
    long RecordId,
    DateTime TimeUtc,
    byte Level,
    ushort ProviderId,
    uint EventId);

public sealed record EventDataItem(string? Name, string Value);

/// <summary>Everything decoded from one event record.</summary>
public sealed record EventDetail
{
    public required EventSummary Summary { get; init; }
    public required string Provider { get; init; }
    public Guid? ProviderGuid { get; init; }
    public string? Channel { get; init; }
    public string? Computer { get; init; }
    public string? UserSid { get; init; }
    public int Version { get; init; }
    public int Qualifiers { get; init; }
    public int Task { get; init; }
    public int Opcode { get; init; }
    public ulong Keywords { get; init; }
    public int? ProcessId { get; init; }
    public int? ThreadId { get; init; }
    public Guid? ActivityId { get; init; }
    public Guid? RelatedActivityId { get; init; }

    /// <summary>Name of the data section: <c>EventData</c>, <c>UserData</c>, or <c>null</c> when the event carries none.</summary>
    public string? DataSection { get; init; }

    public IReadOnlyList<EventDataItem> Data { get; init; } = Array.Empty<EventDataItem>();

    /// <summary>The event rendered as XML, always available and authoritative.</summary>
    public required string Xml { get; init; }

    /// <summary>
    /// Message produced from the local machine's provider metadata, or <c>null</c> when the provider is not
    /// installed here. It may differ from what the originating machine would display.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>The record could not be fully decoded; <see cref="Xml"/> then carries what could be recovered.</summary>
    public string? DecodeError { get; init; }
}

public sealed record EventLogIssue(string Message, long? FileOffset = null);

/// <summary>Random access to the events of an event log without holding their content in memory.</summary>
public interface IEventLogSource
{
    int Count { get; }

    /// <summary>Providers seen in the log with their event counts, most frequent first.</summary>
    IReadOnlyList<EventLogProvider> Providers { get; }

    string ProviderName(ushort providerId);

    EventSummary GetSummary(int index);

    /// <summary>Position of the event with this record id, or -1.</summary>
    int FindByRecordId(long recordId);

    /// <summary>Decodes the event at <paramref name="index"/>. Never throws for corrupt content; see <see cref="EventDetail.DecodeError"/>.</summary>
    EventDetail ReadDetail(int index);

    /// <summary>
    /// Plain text of the event (provider, data values) used for in-log text filtering. Empty if undecodable.
    /// With <paramref name="includeMessage"/> the message rendered from local provider metadata is appended, which is
    /// considerably slower because every event goes through the metadata lookup.
    /// </summary>
    string ReadSearchText(int index, bool includeMessage);
}

/// <summary>Renders an event's human-readable message from provider metadata.</summary>
public interface IEventMessageFormatter
{
    /// <returns>The message, or <c>null</c> when none is available for this provider/event.</returns>
    /// <param name="qualifiers">The <c>Qualifiers</c> attribute of the event id; present only for classic (legacy API) events.</param>
    string? Format(string provider, uint eventId, int? qualifiers, int version, IReadOnlyList<EventDataItem> data);
}

public sealed record EventLogDocument : DiagnosticDocument
{
    public required IEventLogSource Source { get; init; }

    /// <summary>Problems met while indexing (bad chunks, bad records, checksum mismatches).</summary>
    public IReadOnlyList<EventLogIssue> Issues { get; init; } = Array.Empty<EventLogIssue>();

    public int TotalIssueCount { get; init; }

    /// <summary>The header says the log was not cleanly closed (e.g. copied from a live system).</summary>
    public bool IsDirty { get; init; }

    public string? FormatVersion { get; init; }
}
