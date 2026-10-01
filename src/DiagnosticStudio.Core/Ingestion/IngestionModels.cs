namespace DiagnosticStudio.Core.Ingestion;

public enum IngestionStage
{
    Validating = 0,
    Reading,
    Discovering,
    Extracting,
    Classifying,
    Completed,
}

public enum IngestionIssueSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>A non-fatal problem encountered while ingesting a bundle. Surfaced in the Output panel.</summary>
public sealed record IngestionIssue
{
    public required IngestionStage Stage { get; init; }
    public required IngestionIssueSeverity Severity { get; init; }

    /// <summary>Provenance-style description of the artifact or archive entry involved.</summary>
    public required string Subject { get; init; }

    /// <summary>Archive provider / classifier that raised the issue, when applicable.</summary>
    public string? Component { get; init; }

    public required string Message { get; init; }
    public string? Detail { get; init; }
}

public sealed record IngestionProgress
{
    public required IngestionStage Stage { get; init; }
    public required string Message { get; init; }
    public int ArtifactsDiscovered { get; init; }
    public int ArchivesExtracted { get; init; }
    public long BytesExtracted { get; init; }
}

/// <summary>Configurable safety limits applied to everything extracted from a bundle.</summary>
public sealed record ExtractionLimits
{
    public long MaxTotalExtractedBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaxSingleFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxFileCount { get; init; } = 200_000;
    public int MaxNestingDepth { get; init; } = 8;
}

public sealed record IngestionOptions
{
    public ExtractionLimits Limits { get; init; } = new();

    /// <summary>Parent directory for per-investigation working directories. Defaults to the system temp directory.</summary>
    public string? WorkspaceRoot { get; init; }
}
