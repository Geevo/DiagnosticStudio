using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Fallback parser. Unsupported artifacts still belong in the investigation, so this produces an
/// <see cref="UnsupportedDocument"/> that says why there is no richer view yet.
/// </summary>
public sealed class UnsupportedArtifactParser : IDiagnosticParser
{
    public bool CanHandle(DiagnosticArtifact artifact) => true;

    public Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken) =>
        Task.FromResult<DiagnosticDocument>(new UnsupportedDocument
        {
            Artifact = artifact,
            Reason = DescribeReason(artifact),
        });

    private static string DescribeReason(DiagnosticArtifact artifact) => artifact.ArtifactType switch
    {
        ArtifactType.Trace => "This ETL trace could not be decoded. See the Output panel for the reason.",
        ArtifactType.Archive when !artifact.IsContainer =>
            "This archive was not opened. See the Output panel for the reason.",
        ArtifactType.Archive => "Archive contents are listed as separate artifacts in the explorer.",
        ArtifactType.Binary => "Binary content has no viewer.",
        _ => "No viewer is available for this artifact type yet.",
    };
}
