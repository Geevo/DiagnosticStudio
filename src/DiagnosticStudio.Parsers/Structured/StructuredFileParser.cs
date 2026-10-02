using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers.Structured;

/// <summary>
/// Opens XML and JSON artifacts as a node tree next to their raw text. Files that are too large for a tree, and files
/// that are not valid, are not handled here (or fail): the text parser registered after this one then opens the raw
/// source, so the engineer always gets the content.
/// </summary>
public sealed class StructuredFileParser : IDiagnosticParser
{
    /// <summary>Larger files open as plain text. A tree of a file this size already holds a few hundred MB of objects.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType is ArtifactType.Xml or ArtifactType.Json
        && artifact.ExtractedPath is { } path
        && IsWithinLimit(path);

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var path = artifact.ExtractedPath!;
        var format = artifact.ArtifactType == ArtifactType.Xml ? StructuredFormat.Xml : StructuredFormat.Json;

        // The raw text is indexed first: it is what the engineer falls back to, and what every line link points into.
        var raw = await IndexedTextFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);

        var (root, count, stop) = await Task.Run(
            () =>
            {
                if (format == StructuredFormat.Xml)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    try
                    {
                        return XmlStructureReader.ReadTolerant(stream, cancellationToken);
                    }
                    catch (System.Xml.XmlException ex)
                    {
                        throw new InvalidDataException($"Not well-formed XML: {ex.Message}", ex);
                    }
                }

                var (jsonRoot, jsonCount) = JsonStructureReader.Read(File.ReadAllBytes(path), cancellationToken);
                return (jsonRoot, jsonCount, (XmlStructureReader.ReadStop?)null);
            },
            cancellationToken).ConfigureAwait(false);

        return new StructuredDocument
        {
            Artifact = artifact,
            Format = format,
            Root = root,
            RawSource = raw,
            NodeCount = count,
            ReadProblem = stop?.Message,
            ReadProblemLine = stop?.Line ?? 0,
        };
    }

    private static bool IsWithinLimit(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
