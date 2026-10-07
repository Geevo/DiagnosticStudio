using System.Windows;
using DiagnosticStudio.App.ViewModels.About;
using DiagnosticStudio.App.Views;

namespace DiagnosticStudio.App.Services;

public interface IAboutDialogService
{
    /// <summary>Shows the About dialog and returns when it is closed.</summary>
    void Show();
}

public sealed class AboutDialogService : IAboutDialogService
{
    private readonly AppInfo _info;
    private readonly IStaleWorkspaceCleanup _cleanup;

    public AboutDialogService(AppInfo info, IStaleWorkspaceCleanup cleanup)
    {
        _info = info;
        _cleanup = cleanup;
    }

    public void Show()
    {
        var viewModel = new AboutViewModel(_info, _cleanup);
        var window = new AboutWindow { DataContext = viewModel, Owner = Application.Current.MainWindow };
        window.Loaded += async (_, _) => await viewModel.LoadAsync();
        window.ShowDialog();
    }
}
