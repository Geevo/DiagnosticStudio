using System.IO;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.App.ViewModels.About;

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IStaleWorkspaceCleanup _cleanup;

    public AboutViewModel(AppInfo info, IStaleWorkspaceCleanup cleanup)
    {
        Name = info.Name;
        VersionText = "Version " + info.Version;
        RepositoryUrl = info.RepositoryUrl;
        _cleanup = cleanup;
        _cleanupText = "Checking for leftover extracted archives...";
    }

    public string Name { get; }
    public string VersionText { get; }
    public string RepositoryUrl { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearStaleCommand))]
    private bool _hasStale;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearStaleCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _cleanupText;

    /// <summary>Looks for leftovers; the dialog calls this when it opens.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Show(await _cleanup.SurveyAsync().ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HasStale = false;
            CleanupText = "Could not check for leftover extracted archives: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearStale))]
    private async Task ClearStale()
    {
        IsBusy = true;
        try
        {
            var result = await _cleanup.ClearAsync().ConfigureAwait(true);
            var after = await _cleanup.SurveyAsync().ConfigureAwait(true);
            HasStale = after.Stale > 0;
            CleanupText = result.Failed > 0
                ? Format($"Removed {result.Removed:N0}; {result.Failed:N0} could not be removed. {result.Errors.FirstOrDefault()}")
                : Format($"Removed {result.Removed:N0} leftover extracted {Archives(result.Removed)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanupText = "Could not clear leftover extracted archives: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClearStale() => HasStale && !IsBusy;

    private void Show(WorkspaceSurvey survey)
    {
        HasStale = survey.Stale > 0;
        CleanupText = survey.Stale == 0
            ? "No leftover extracted archives."
            : Format($"{survey.Stale:N0} leftover extracted {Archives(survey.Stale)} from earlier sessions, using {SizeText(survey.Bytes)}.");
    }

    private static string Archives(int count) => count == 1 ? "archive" : "archives";

    private static string Format(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);

    internal static string SizeText(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} bytes")
            : string.Create(CultureInfo.CurrentCulture, $"{size:0.#} {units[unit]}");
    }
}
