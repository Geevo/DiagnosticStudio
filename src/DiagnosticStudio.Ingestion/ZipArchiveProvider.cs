using System.IO.Compression;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;

namespace DiagnosticStudio.Ingestion;

/// <summary>Extracts ZIP archives with traversal, size, count and malformed-entry protection.</summary>
public sealed class ZipArchiveProvider : IArchiveProvider
{
    private const int BufferSize = 81920;

    public string Name => "ZIP";

    public bool CanOpen(DiagnosticArtifact artifact) =>
        artifact.ArtifactType == ArtifactType.Archive
        && artifact.Subtype == ArchiveFormats.Zip
        && artifact.ExtractedPath is not null;

    public async Task<IReadOnlyList<ExtractedArchiveEntry>> ExtractAsync(
        DiagnosticArtifact artifact,
        string destinationDirectory,
        ArchiveExtractionContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<ExtractedArchiveEntry>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(destinationDirectory);

        using var archive = ZipFile.OpenRead(artifact.ExtractedPath!);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subject = artifact.ProvenanceDisplay + " → " + entry.FullName;

            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            if (!SafeEntryPath.TryResolve(destinationDirectory, entry.FullName, out var target, out var reason))
            {
                Report(context, IngestionIssueSeverity.Warning, subject, "Entry skipped: " + reason);
                continue;
            }

            if (entry.Length > context.Budget.Limits.MaxSingleFileBytes)
            {
                Report(
                    context,
                    IngestionIssueSeverity.Warning,
                    subject,
                    $"Entry skipped: declared size {entry.Length:N0} bytes exceeds the single-file limit.");
                continue;
            }

            var fileLimit = context.Budget.TryReserveFile();
            if (fileLimit is { } fileViolation)
            {
                Report(context, IngestionIssueSeverity.Error, subject, "Extraction stopped: " + fileViolation.Message);
                break;
            }

            target = ExtractionFiles.MakeUnique(target, used);

            var outcome = await ExtractEntryAsync(entry, target, context, cancellationToken).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case EntryStatus.Extracted:
                    results.Add(new ExtractedArchiveEntry(entry.FullName.Replace('\\', '/'), target, outcome.Bytes));
                    break;
                case EntryStatus.Skipped:
                    Report(context, IngestionIssueSeverity.Warning, subject, outcome.Message!);
                    break;
                case EntryStatus.Stop:
                    Report(context, IngestionIssueSeverity.Error, subject, "Extraction stopped: " + outcome.Message);
                    return results;
            }
        }

        return results;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static async Task<EntryOutcome> ExtractEntryAsync(
        ZipArchiveEntry entry,
        string target,
        ArchiveExtractionContext context,
        CancellationToken cancellationToken)
    {
        long written = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            await using (var input = entry.Open())
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;

                    // Check as bytes arrive: the declared size in the central directory can be forged.
                    var limit = context.Budget.TryAddBytes(read, written);
                    if (limit is { } violation)
                    {
                        output.Close();
                        ExtractionFiles.TryDelete(target);
                        return new EntryOutcome(
                            violation.StopsExtraction ? EntryStatus.Stop : EntryStatus.Skipped,
                            0,
                            violation.Message);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            return new EntryOutcome(EntryStatus.Extracted, written, null);
        }
        catch (OperationCanceledException)
        {
            ExtractionFiles.TryDelete(target);
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException
                                       or UnauthorizedAccessException)
        {
            ExtractionFiles.TryDelete(target);
            return new EntryOutcome(EntryStatus.Skipped, 0, "Entry skipped: " + ex.Message);
        }
    }

    private static void Report(
        ArchiveExtractionContext context,
        IngestionIssueSeverity severity,
        string subject,
        string message) =>
        context.ReportIssue(new IngestionIssue
        {
            Stage = IngestionStage.Extracting,
            Severity = severity,
            Subject = subject,
            Component = "ZIP",
            Message = message,
        });

    private enum EntryStatus
    {
        Extracted,
        Skipped,
        Stop,
    }

    private readonly record struct EntryOutcome(EntryStatus Status, long Bytes, string? Message);
}
