using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;

namespace DiagnosticStudio.Core.Archives;

/// <summary>One file written out of an archive.</summary>
/// <param name="EntryPath">Path of the entry as stored in the archive (separators normalised to '/').</param>
/// <param name="ExtractedPath">Absolute path of the working copy, always inside the destination directory.</param>
/// <param name="Size">Bytes actually written.</param>
public sealed record ExtractedArchiveEntry(string EntryPath, string ExtractedPath, long Size);

public enum ExtractionLimitKind
{
    FileCount,
    SingleFileSize,
    TotalSize,
}

/// <summary>A safety limit that was exceeded. <see cref="ExtractionLimitKind.SingleFileSize"/> only affects one entry; the others end extraction.</summary>
public readonly record struct ExtractionLimitViolation(ExtractionLimitKind Kind, string Message)
{
    public bool StopsExtraction => Kind != ExtractionLimitKind.SingleFileSize;
}

/// <summary>
/// Tracks totals across a whole ingestion run so limits apply to the bundle as a whole,
/// not per archive. Safe for concurrent use.
/// </summary>
public sealed class ExtractionBudget
{
    private long _bytes;
    private int _files;

    public ExtractionBudget(ExtractionLimits limits)
    {
        Limits = limits;
    }

    public ExtractionLimits Limits { get; }
    public long BytesExtracted => Interlocked.Read(ref _bytes);
    public int FilesExtracted => Volatile.Read(ref _files);

    /// <summary>Reserves one file slot. Returns the violation if the limit is exceeded, otherwise <c>null</c>.</summary>
    public ExtractionLimitViolation? TryReserveFile()
    {
        if (Interlocked.Increment(ref _files) > Limits.MaxFileCount)
        {
            Interlocked.Decrement(ref _files);
            return new ExtractionLimitViolation(
                ExtractionLimitKind.FileCount,
                $"File count limit of {Limits.MaxFileCount:N0} reached.");
        }

        return null;
    }

    /// <summary>Accounts for bytes as they are written. Returns the violation if a limit is exceeded.</summary>
    public ExtractionLimitViolation? TryAddBytes(long count, long fileBytesSoFar)
    {
        if (fileBytesSoFar > Limits.MaxSingleFileBytes)
        {
            return new ExtractionLimitViolation(
                ExtractionLimitKind.SingleFileSize,
                $"Single file size limit of {Limits.MaxSingleFileBytes:N0} bytes exceeded.");
        }

        if (Interlocked.Add(ref _bytes, count) > Limits.MaxTotalExtractedBytes)
        {
            return new ExtractionLimitViolation(
                ExtractionLimitKind.TotalSize,
                $"Total extracted size limit of {Limits.MaxTotalExtractedBytes:N0} bytes exceeded.");
        }

        return null;
    }
}

/// <summary>Per-archive context handed to an <see cref="IArchiveProvider"/>.</summary>
public sealed class ArchiveExtractionContext
{
    public ArchiveExtractionContext(
        ExtractionBudget budget,
        int nestingDepth,
        Action<IngestionIssue> reportIssue)
    {
        Budget = budget;
        NestingDepth = nestingDepth;
        ReportIssue = reportIssue;
    }

    public ExtractionBudget Budget { get; }

    /// <summary>Depth of the archive being extracted (0 for the root bundle).</summary>
    public int NestingDepth { get; }

    /// <summary>Records a non-fatal problem; extraction of other entries continues.</summary>
    public Action<IngestionIssue> ReportIssue { get; }
}

public interface IArchiveProvider
{
    string Name { get; }

    bool CanOpen(DiagnosticArtifact artifact);

    /// <summary>
    /// Safely extracts the archive into <paramref name="destinationDirectory"/>. Must never write outside it.
    /// Unsafe or malformed entries are skipped and reported through the context rather than thrown.
    /// </summary>
    Task<IReadOnlyList<ExtractedArchiveEntry>> ExtractAsync(
        DiagnosticArtifact artifact,
        string destinationDirectory,
        ArchiveExtractionContext context,
        CancellationToken cancellationToken);
}
