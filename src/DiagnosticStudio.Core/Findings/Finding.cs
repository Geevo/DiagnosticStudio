using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Core.Findings;

public enum FindingSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>A single piece of source evidence backing a finding.</summary>
public sealed record FindingEvidence
{
    public required DiagnosticLocation Location { get; init; }
    public string? Description { get; init; }
}

/// <summary>Deterministic, evidence-backed observation produced by an <see cref="Rules.IDiagnosticRule"/>.</summary>
public sealed record Finding
{
    public required string Id { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public IReadOnlyList<FindingEvidence> Evidence { get; init; }
        = Array.Empty<FindingEvidence>();

    public IReadOnlyList<string> Tags { get; init; }
        = Array.Empty<string>();

    public string? Notes { get; init; }
}
