using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels.HtmlViewer;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DiagnosticStudio.App.Views;

/// <summary>
/// Draws an untrusted HTML file in the system's WebView2 browser component with everything switched off that could run
/// it, fetch from anywhere or navigate away (see <see cref="HtmlSandbox"/>). If the component is missing or fails, the
/// view-model is told and the source is shown instead.
/// </summary>
public partial class HtmlViewerView : UserControl
{
    private static Task<CoreWebView2Environment>? _environment;

    private HtmlViewerViewModel? _viewModel;
    private WebView2? _web;
    private bool _allowOwnNavigation;

    public HtmlViewerView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _viewModel = e.NewValue as HtmlViewerViewModel;

    // One browser environment for the whole application, with no saved profile: nothing from a page is kept.
    private static Task<CoreWebView2Environment> Environment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DiagnosticStudio", "WebView2"));

    private async void OnWebHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { CanRender: true, Markup: { } markup } vm || _web is not null)
        {
            return;
        }

        try
        {
            var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            _web = web;
            WebHost.Children.Add(web);

            var environment = await Environment().ConfigureAwait(true);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await web.EnsureCoreWebView2Async(environment, options).ConfigureAwait(true);

            Lock(web.CoreWebView2, environment, vm);
            _allowOwnNavigation = true;
            web.NavigateToString(HtmlSandbox.Prepare(markup));
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or IOException)
        {
            Discard();
            vm.ReportRenderFailure(
                ex is WebView2RuntimeNotFoundException
                    ? "The Microsoft Edge WebView2 component is not installed, so the page cannot be drawn. The source is shown."
                    : "The page could not be drawn: " + ex.Message + " The source is shown.");
        }
    }

    // Everything that could run the page, load from elsewhere or leave it is off or refused.
    private void Lock(CoreWebView2 core, CoreWebView2Environment environment, HtmlViewerViewModel vm)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsZoomControlEnabled = true;

        // The only navigation allowed is the one that put the page there; links, redirects and meta refreshes are refused.
        core.NavigationStarting += (_, args) =>
        {
            if (_allowOwnNavigation)
            {
                _allowOwnNavigation = false;
                return;
            }

            args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;

        // Every request the page makes is answered "forbidden" unless it is data inside the page itself.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!HtmlSandbox.IsAllowedRequest(args.Request.Uri))
            {
                args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
        };

        core.ProcessFailed += (_, _) => Dispatcher.BeginInvoke(() =>
            vm.ReportRenderFailure("The browser component stopped while drawing the page. The source is shown."));
    }

    private void OnWebHostUnloaded(object sender, RoutedEventArgs e) => Discard();

    private void Discard()
    {
        if (_web is null)
        {
            return;
        }

        WebHost.Children.Remove(_web);
        _web.Dispose();
        _web = null;
    }
}
