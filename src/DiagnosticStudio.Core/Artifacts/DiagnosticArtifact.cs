namespace DiagnosticStudio.Core.Artifacts;

/// <summary>
/// A discovered item in the investigation.
/// </summary>
public sealed record DiagnosticArtifact
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Path of the artifact as it appears inside its immediate container (archive entry name or
    /// path relative to the input directory). For the root bundle this is the input path.
    /// </summary>
    public required string OriginalPath { get; init; }

    /// <summary>Location of the working copy on disk. For directory inputs this is the original file and must be treated as read-only.</summary>
    public string? ExtractedPath { get; init; }

    /// <summary>The archive artifact this one was extracted from, if any.</summary>
    public Guid? ParentContainerId { get; init; }

    /// <summary>
    /// Full chain from the root bundle to this artifact, one element per hop/path segment,
    /// e.g. <c>Bundle.zip, mdmlogs.cab, Logs, agent.log</c>.
    /// </summary>
    public IReadOnlyList<string> Provenance { get; init; } = Array.Empty<string>();

    /// <summary>Number of archives this artifact is nested inside (0 for the root bundle or top-level directory content).</summary>
    public int NestingDepth { get; init; }

    public required ArtifactType ArtifactType { get; init; }
    public string? Category { get; init; }
    public string? Subtype { get; init; }

    /// <summary>True when this artifact is an archive that was opened as a container.</summary>
    public bool IsContainer { get; init; }

    public long Size { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();

    public string ProvenanceDisplay => string.Join(" → ", Provenance);
}
