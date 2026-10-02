using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers.Etl;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Opens <c>.etl</c> trace files as an <see cref="EventLogDocument"/>. The file is read with the Windows trace API; it is
/// only replayed from disk, nothing in it is run and no trace session is started. A file that is not a trace log, or a
/// machine that cannot read one, is reported through the loader as a parse failure with the reason.
/// </summary>
public sealed class EtlParser : IDiagnosticParser
{
    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType == ArtifactType.Trace && artifact.ExtractedPath is not null;

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        EtlFile file;
        try
        {
            file = await EtlFile.OpenAsync(artifact.ExtractedPath!, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        return new EventLogDocument
        {
            Artifact = artifact,
            Source = file,
            Notes = file.Notes,
        };
    }
}
