using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// The working directories earlier sessions left behind (an archive's extracted files, kept while it was open).
/// Never includes the directory of any open archive, in this or another running instance.
/// </summary>
public interface IStaleWorkspaceCleanup
{
    Task<WorkspaceSurvey> SurveyAsync();

    Task<WorkspaceSweepResult> ClearAsync();
}

public sealed class StaleWorkspaceCleanup : IStaleWorkspaceCleanup
{
    private readonly string? _root;

    public StaleWorkspaceCleanup(string? root = null) => _root = root;

    public Task<WorkspaceSurvey> SurveyAsync() => WorkspaceJanitor.SurveyAsync(_root);

    public Task<WorkspaceSweepResult> ClearAsync() => WorkspaceJanitor.SweepStaleAsync(_root);
}
