using System.Collections.Concurrent;
using System.Globalization;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Rules;

/// <summary>
/// Opens each file once (through the shared document cache, so nothing is parsed twice) and records the ones that
/// could not be fully read: empty, no viewer, partly parsed, or failed. A file that cannot be opened at all is a
/// result here, never an exception.
/// </summary>
public sealed class FileHealthService : IFileHealthService
{
    private const int MaxParallelism = 4;

    private readonly IDocumentLoader _loader;
    private readonly IBackgroundWorkGate _gate;

    public FileHealthService(IDocumentLoader loader, IBackgroundWorkGate? gate = null)
    {
        _loader = loader;
        _gate = gate ?? UnlimitedWorkGate.Instance;
    }

    public async Task<IReadOnlyList<FileProblem>> CheckAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<FileHealthProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = artifacts.Where(a => !a.IsContainer).ToList();
        var problems = new ConcurrentBag<FileProblem>();
        var done = 0;
        progress?.Report(new FileHealthProgress(0, files.Count));

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
            async (artifact, token) =>
            {
                using (await _gate.EnterAsync(token).ConfigureAwait(false))
                {
                    foreach (var problem in await CheckOneAsync(artifact, token).ConfigureAwait(false))
                    {
                        problems.Add(problem);
                    }
                }

                progress?.Report(new FileHealthProgress(Interlocked.Increment(ref done), files.Count));
            }).ConfigureAwait(false);

        return problems
            .OrderByDescending(p => p.Kind)
            .ThenBy(p => p.Artifact.ProvenanceDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Artifact.Id)
            .ToList();
    }

    private async Task<IEnumerable<FileProblem>> CheckOneAsync(DiagnosticArtifact artifact, CancellationToken token)
    {
        var found = new List<FileProblem>();
        var here = DiagnosticLocation.ForArtifact(artifact.Id);

        if (IsEmptyOnDisk(artifact))
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Empty,
                "The file is empty (0 bytes).",
                "Nothing to read. It was empty when it was collected, or it was cut short; collect it again if it should have content.",
                here));
            return found;
        }

        DocumentLoadResult loaded;
        try
        {
            loaded = await _loader.LoadAsync(artifact, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Failed,
                "The file could not be opened: " + ex.Message,
                "Check that the file is readable, then open it again. Open it in Notepad from the explorer's right-click menu to see the raw content.",
                here));
            return found;
        }

        if (loaded.FailureMessage is { } failure)
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Failed,
                failure,
                "The file is shown as plain text where that is possible, so the content is still readable. The raw source is always available.",
                here));
        }

        switch (loaded.Document)
        {
            case UnsupportedDocument unsupported when loaded.FailureMessage is null:
                found.Add(new FileProblem(
                    artifact,
                    FileProblemKind.NoViewer,
                    unsupported.Reason,
                    "Use Open in Notepad or Show in File Explorer from the right-click menu to look at it with another tool.",
                    here));
                break;

            case RegistryDocument registry:
                AddRegistryProblems(artifact, registry, found);
                break;

            case EventLogDocument events:
                AddEventLogProblems(artifact, events, found);
                break;

            case StructuredDocument { ReadProblem: { } problem } structured:
                found.Add(new FileProblem(
                    artifact,
                    FileProblemKind.Partial,
                    string.Create(
                        CultureInfo.CurrentCulture,
                        $"Only part of this file could be read: {problem}"),
                    "The tree shows what came before that point and the raw source has everything. A file that stops short like this was usually cut off while it was being written.",
                    structured.ReadProblemLine > 0
                        ? DiagnosticLocation.ForLine(artifact.Id, structured.ReadProblemLine)
                        : DiagnosticLocation.ForArtifact(artifact.Id)));
                break;

            case TableDocument { UnreadLines: > 0 } table:
                found.Add(new FileProblem(
                    artifact,
                    FileProblemKind.Caution,
                    table.UnreadLines == 1
                        ? string.Create(CultureInfo.CurrentCulture, $"1 line is not part of any record, at line {table.FirstUnreadLine:N0}. It is visible in the raw source.")
                        : string.Create(CultureInfo.CurrentCulture, $"{table.UnreadLines:N0} lines are not part of any record. First at line {table.FirstUnreadLine:N0}. They are visible in the raw source."),
                    "The records were read; open the raw source at the first such line to see what is there.",
                    DiagnosticLocation.ForLine(artifact.Id, table.FirstUnreadLine)));
                break;
        }

        return found;
    }

    private static bool IsEmptyOnDisk(DiagnosticArtifact artifact)
    {
        try
        {
            return artifact.ExtractedPath is { } path && new FileInfo(path) is { Exists: true, Length: 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AddRegistryProblems(DiagnosticArtifact artifact, RegistryDocument registry, List<FileProblem> found)
    {
        var culture = CultureInfo.CurrentCulture;
        if (registry.TotalIssueCount > 0)
        {
            var first = registry.Issues.Count > 0 ? registry.Issues[0].Line : 0;
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Partial,
                registry.TotalIssueCount == 1
                    ? string.Create(culture, $"1 line could not be parsed, at line {first:N0}. It is visible in the raw source.")
                    : string.Create(culture, $"{registry.TotalIssueCount:N0} lines could not be parsed. First at line {first:N0}. They are visible in the raw source."),
                "The rest of the file was read. Open the raw source at the first unparsed line to see what the exporting tool wrote there.",
                first > 0 ? DiagnosticLocation.ForLine(artifact.Id, first) : DiagnosticLocation.ForArtifact(artifact.Id)));
        }

        if (registry.IsTruncated)
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Partial,
                string.Create(culture, $"Parsing stopped at line {registry.TruncatedAtLine:N0} because the file is larger than the size limits."),
                "Keys and values after that line are only in the raw source. Search covers the raw text.",
                registry.TruncatedAtLine > 0 ? DiagnosticLocation.ForLine(artifact.Id, registry.TruncatedAtLine) : DiagnosticLocation.ForArtifact(artifact.Id)));
        }
    }

    private static void AddEventLogProblems(DiagnosticArtifact artifact, EventLogDocument events, List<FileProblem> found)
    {
        var culture = CultureInfo.CurrentCulture;
        if (events.TotalIssueCount > 0)
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Partial,
                string.Create(culture, $"{events.TotalIssueCount:N0} damaged chunks or records were skipped while reading this log."),
                "The events that could be read are listed. The Output panel has the details. A log copied while Windows was writing to it often has a damaged tail.",
                DiagnosticLocation.ForArtifact(artifact.Id)));
        }

        foreach (var note in events.Notes)
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Caution,
                note,
                "The raw data of each event is kept and can be searched. Open the file on a machine that has the program the trace came from for full descriptions.",
                DiagnosticLocation.ForArtifact(artifact.Id)));
        }

        if (events.IsDirty)
        {
            found.Add(new FileProblem(
                artifact,
                FileProblemKind.Caution,
                "The log was not cleanly closed, so it may have been copied while Windows was still writing to it. The newest events may be missing.",
                "Collect the log again after the activity you are interested in. Reading the file again cannot clear this: the mark is stored in the file.",
                DiagnosticLocation.ForArtifact(artifact.Id)));
        }
    }
}
