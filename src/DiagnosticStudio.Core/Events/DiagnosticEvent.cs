using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Core.Events;

/// <summary>Normalized timestamped record that can feed a future unified timeline.</summary>
public sealed record DiagnosticEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public string? Severity { get; init; }
    public required string Source { get; init; }
    public string? Category { get; init; }
    public required string Message { get; init; }
    public required DiagnosticLocation Location { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; }
        = new Dictionary<string, string>();
}
