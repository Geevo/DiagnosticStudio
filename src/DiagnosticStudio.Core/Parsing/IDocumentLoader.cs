using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Core.Parsing;

/// <summary>Opens an artifact as a parsed document, choosing a parser and degrading gracefully when parsing fails.</summary>
public interface IDocumentLoader
{
    Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken);
}

/// <param name="Document">Always non-null.</param>
/// <param name="FailureMessage">Set when a parser threw; suitable for the Output panel.</param>
public sealed record DocumentLoadResult(DiagnosticDocument Document, string? FailureMessage);
