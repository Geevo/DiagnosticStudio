using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;

namespace DiagnosticStudio.Core.Rules;

/// <summary>Read-only view of the investigation handed to rules.</summary>
public sealed class DiagnosticContext
{
    public DiagnosticContext(IReadOnlyList<DiagnosticArtifact> artifacts)
    {
        Artifacts = artifacts;
    }

    public IReadOnlyList<DiagnosticArtifact> Artifacts { get; }
}

public interface IDiagnosticRule
{
    string Id { get; }

    Task<IEnumerable<Finding>> EvaluateAsync(
        DiagnosticContext context,
        CancellationToken cancellationToken);
}
