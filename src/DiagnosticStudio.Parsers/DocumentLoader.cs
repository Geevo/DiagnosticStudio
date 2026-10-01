using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Picks the first parser that can handle an artifact. A parser failure degrades to an
/// <see cref="UnsupportedDocument"/>; it never propagates and takes the investigation down.
/// </summary>
public sealed class DocumentLoader : IDocumentLoader
{
    private readonly IReadOnlyList<IDiagnosticParser> _parsers;

    /// <param name="parsers">In priority order; put <see cref="UnsupportedArtifactParser"/> last.</param>
    public DocumentLoader(IEnumerable<IDiagnosticParser> parsers)
    {
        _parsers = parsers.ToList();
    }

    public async Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        // A dedicated parser that throws must not cost the engineer the raw source: try the next capable parser.
        foreach (var parser in _parsers.Where(p => p.CanHandle(artifact)))
        {
            try
            {
                var document = await parser.ParseAsync(artifact, cancellationToken).ConfigureAwait(false);
                return new DocumentLoadResult(document, failures.Count == 0 ? null : string.Join(" ", failures));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{parser.GetType().Name} failed on {artifact.ProvenanceDisplay}: {ex.Message}");
            }
        }

        var reason = failures.Count == 0
            ? "No parser is registered for this artifact."
            : "All parsers failed: " + string.Join(" ", failures);
        return new DocumentLoadResult(
            new UnsupportedDocument { Artifact = artifact, Reason = reason },
            failures.Count == 0 ? null : string.Join(" ", failures));
    }
}
