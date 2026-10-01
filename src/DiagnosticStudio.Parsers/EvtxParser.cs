using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers.Evtx;

namespace DiagnosticStudio.Parsers;

/// <summary>Opens offline <c>.evtx</c> artifacts as an <see cref="EventLogDocument"/>.</summary>
public sealed class EvtxParser : IDiagnosticParser
{
    private readonly IEventMessageFormatter? _formatter;

    public EvtxParser(IEventMessageFormatter? formatter = null)
    {
        _formatter = formatter;
    }

    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType == ArtifactType.EventLog && artifact.ExtractedPath is not null;

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        EvtxFile file;
        try
        {
            file = await EvtxFile.OpenAsync(artifact.ExtractedPath!, _formatter, cancellationToken).ConfigureAwait(false);
        }
        catch (EvtxFormatException ex)
        {
            // Surfaced through the loader as a parser failure; the artifact stays listed with the reason.
            throw new InvalidDataException(ex.Message, ex);
        }

        return new EventLogDocument
        {
            Artifact = artifact,
            Source = file,
            Issues = file.Issues,
            TotalIssueCount = file.TotalIssueCount,
            IsDirty = file.IsDirty,
            FormatVersion = file.FormatVersion,
        };
    }
}
