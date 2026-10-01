using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Core.Parsing;

public interface IDiagnosticParser
{
    bool CanHandle(DiagnosticArtifact artifact);

    Task<DiagnosticDocument> ParseAsync(
        DiagnosticArtifact artifact,
        CancellationToken cancellationToken);
}
