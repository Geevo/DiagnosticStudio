using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Core.Findings;

/// <summary>How a file in the bundle fell short of being fully readable.</summary>
public enum FileProblemKind
{
    /// <summary>No content: the file is 0 bytes.</summary>
    Empty = 0,

    /// <summary>The file is a kind the tool has no viewer for yet.</summary>
    NoViewer,

    /// <summary>
    /// It opened and everything that could be read was read, but something about it is worth knowing: a log copied
    /// while in use, stray lines between records, events with no description here. Not damage the engineer can fix.
    /// </summary>
    Caution,

    /// <summary>It opened, but not all of it could be read (unparsed lines, damaged records, a size limit).</summary>
    Partial,

    /// <summary>A parser failed. The raw text is shown where there is any.</summary>
    Failed,
}

/// <summary>
/// A file that could not be fully read, with what is known about why and what to try. It is about the file, not about
/// the machine the bundle came from, so it is reported separately from rule findings.
/// </summary>
/// <param name="Message">What happened, with numbers where there are any.</param>
/// <param name="Remedy">What the engineer can do about it.</param>
/// <param name="Location">Where to look: the first unparsed line when one is known, otherwise the file.</param>
public sealed record FileProblem(
    DiagnosticArtifact Artifact,
    FileProblemKind Kind,
    string Message,
    string Remedy,
    DiagnosticLocation Location);

public readonly record struct FileHealthProgress(int FilesChecked, int FilesTotal);

public interface IFileHealthService
{
    /// <summary>Opens every file in <paramref name="artifacts"/> and reports the ones that were not fully readable.</summary>
    Task<IReadOnlyList<FileProblem>> CheckAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<FileHealthProgress>? progress,
        CancellationToken cancellationToken);
}
