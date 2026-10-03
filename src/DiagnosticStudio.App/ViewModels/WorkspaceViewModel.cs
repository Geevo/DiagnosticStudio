using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.Core.Ingestion;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>Owns the active investigation and the bundle-loading lifecycle.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly IBundleIngestor _ingestor;
    private readonly IOutputLog _output;
    private CancellationTokenSource? _loadCts;

    public WorkspaceViewModel(IBundleIngestor ingestor, IOutputLog output)
    {
        _ingestor = ingestor;
        _output = output;
    }

    /// <summary>Raised after <see cref="Current"/> changed. The previous workspace is disposed afterwards.</summary>
    public event EventHandler? WorkspaceChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace), nameof(BundleName))]
    private InvestigationWorkspace? _current;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    public bool HasWorkspace => Current is not null;

    public string? BundleName => Current is null ? null : Path.GetFileName(Current.InputPath.TrimEnd('\\', '/'));

    public async Task OpenAsync(string path)
    {
        if (IsLoading)
        {
            return;
        }

        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        ProgressMessage = "Preparing diagnostics...";
        ProgressDetail = string.Empty;
        _output.Write(OutputSeverity.Information, "Ingestion", "Opening " + path);

        var progress = new Progress<IngestionProgress>(p =>
        {
            ProgressMessage = p.Message;
            ProgressDetail = $"{p.ArtifactsDiscovered:N0} artifacts · {p.ArchivesExtracted:N0} archives opened";
        });

        try
        {
            var workspace = await _ingestor
                .IngestAsync(path, new IngestionOptions(), progress, _loadCts.Token)
                .ConfigureAwait(true);

            var previous = Current;
            Current = workspace;
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            DisposeInBackground(previous);

            foreach (var issue in workspace.Issues)
            {
                _output.Write(ToOutputSeverity(issue.Severity), issue.Component ?? "Ingestion",
                    $"{issue.Subject}: {issue.Message}");
            }

            _output.Write(
                OutputSeverity.Information,
                "Ingestion",
                $"Loaded {BundleName}: {workspace.Artifacts.Count:N0} artifacts, {workspace.Issues.Count:N0} issues.");
        }
        catch (OperationCanceledException)
        {
            _output.Write(OutputSeverity.Warning, "Ingestion", "Opening was cancelled.");
        }
        catch (Exception ex)
        {
            _output.Write(OutputSeverity.Error, "Ingestion", $"Could not open {path}: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            _loadCts.Dispose();
            _loadCts = null;
        }
    }

    [RelayCommand]
    private void CancelLoad() => _loadCts?.Cancel();

    public void Close()
    {
        var previous = Current;
        if (previous is null)
        {
            return;
        }

        Current = null;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        DisposeInBackground(previous);
    }

    private Task _cleanup = Task.CompletedTask;

    /// <summary>
    /// Removal of the previous workspace's extracted files. Deleting thousands of files (and having a virus scanner
    /// look at each) can take seconds, so it never runs on the interface thread. Removals run one after another.
    /// </summary>
    public Task PendingCleanup => _cleanup;

    private void DisposeInBackground(InvestigationWorkspace? previous)
    {
        if (previous is null)
        {
            return;
        }

        var before = _cleanup;
        _cleanup = Task.Run(async () =>
        {
            await before.ConfigureAwait(false);
            try
            {
                previous.Dispose();
            }
            catch (Exception ex)
            {
                _output.Write(OutputSeverity.Warning, "Housekeeping", "Could not remove the previous working folder: " + ex.Message);
            }
        });
    }

    private static OutputSeverity ToOutputSeverity(IngestionIssueSeverity severity) => severity switch
    {
        IngestionIssueSeverity.Error => OutputSeverity.Error,
        IngestionIssueSeverity.Warning => OutputSeverity.Warning,
        _ => OutputSeverity.Information,
    };
}
