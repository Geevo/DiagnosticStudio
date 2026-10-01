using DiagnosticStudio.Core.Artifacts;

namespace DiagnosticStudio.Core.Ingestion;

/// <summary>
/// The result of ingesting a bundle: artifact catalogue, issues and the app-owned working directory.
/// Disposing removes only the working directory the application created, never the original input.
/// </summary>
public sealed class InvestigationWorkspace : IDisposable
{
    private readonly Action? _cleanup;
    private readonly Dictionary<Guid, DiagnosticArtifact> _byId;
    private bool _disposed;

    public InvestigationWorkspace(
        Guid id,
        string inputPath,
        string workingDirectory,
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IReadOnlyList<IngestionIssue> issues,
        Action? cleanup = null)
    {
        Id = id;
        InputPath = inputPath;
        WorkingDirectory = workingDirectory;
        Artifacts = artifacts;
        Issues = issues;
        _cleanup = cleanup;
        _byId = artifacts.ToDictionary(a => a.Id);
    }

    public Guid Id { get; }
    public string InputPath { get; }
    public string WorkingDirectory { get; }
    public IReadOnlyList<DiagnosticArtifact> Artifacts { get; }
    public IReadOnlyList<IngestionIssue> Issues { get; }

    public DiagnosticArtifact? Find(Guid artifactId) =>
        _byId.TryGetValue(artifactId, out var artifact) ? artifact : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cleanup?.Invoke();
        }
        catch (IOException)
        {
            // Best effort: files may still be held open by a viewer. Leftovers live under the app temp root.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public interface IBundleIngestor
{
    Task<InvestigationWorkspace> IngestAsync(
        string inputPath,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress,
        CancellationToken cancellationToken);
}
