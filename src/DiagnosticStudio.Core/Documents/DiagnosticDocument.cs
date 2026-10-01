using DiagnosticStudio.Core.Artifacts;

namespace DiagnosticStudio.Core.Documents;

/// <summary>
/// Parsed domain representation of an artifact. The UI picks a viewer based on the concrete type;
/// documents never reference UI types.
/// </summary>
public abstract record DiagnosticDocument
{
    public required DiagnosticArtifact Artifact { get; init; }
}

/// <summary>Placeholder for artifacts without a parser. Unsupported artifacts still appear in the explorer.</summary>
public sealed record UnsupportedDocument : DiagnosticDocument
{
    public required string Reason { get; init; }
}
