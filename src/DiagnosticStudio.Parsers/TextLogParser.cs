using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Opens text-like artifacts as an indexed, line-addressable document. Structured formats (XML, JSON,
/// HTML, REG) are also accepted so their raw source is always viewable; register their dedicated
/// parsers before this one.
/// </summary>
public sealed class TextLogParser : IDiagnosticParser
{
    private static readonly HashSet<ArtifactType> Handled = new()
    {
        ArtifactType.TextLog,
        ArtifactType.CommandOutput,
        ArtifactType.Xml,
        ArtifactType.Json,
        ArtifactType.Html,
        ArtifactType.RegistryExport,
    };

    public bool CanHandle(DiagnosticArtifact artifact) =>
        Handled.Contains(artifact.ArtifactType) && artifact.ExtractedPath is not null;

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var source = await IndexedTextFile.OpenAsync(artifact.ExtractedPath!, cancellationToken).ConfigureAwait(false);
        return new TextDocument { Artifact = artifact, Lines = source };
    }
}
