using System.Windows;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.Search;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Core.Timeline;
using DiagnosticStudio.Timeline;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Ingestion;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Parsers.Structured;
using DiagnosticStudio.Search;
using Microsoft.Extensions.DependencyInjection;

namespace DiagnosticStudio.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ThemeService? _theme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MenuDropAlignment.KeepRight();

        // Before any window exists, so the first frame is already in the user's light/dark mode.
        _theme = new ThemeService(this);
        _theme.Start();

        _services = ConfigureServices();
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        _ = SweepStaleWorkspacesAsync(_services.GetRequiredService<IOutputLog>());

        // A path argument (e.g. "Open with") behaves like a drag/drop.
        if (e.Args.Length > 0)
        {
            _services.GetRequiredService<MainWindowViewModel>().OpenPathCommand.Execute(e.Args[0]);
        }
    }

    // Reclaims working directories left behind by an instance that crashed or was killed. Housekeeping must
    // never take the application down, so any failure is reported to Output rather than thrown.
    private static async Task SweepStaleWorkspacesAsync(IOutputLog output)
    {
        try
        {
            var result = await WorkspaceJanitor.SweepStaleAsync();
            if (result.Removed > 0)
            {
                output.Write(
                    OutputSeverity.Information,
                    "Housekeeping",
                    $"Removed {result.Removed:N0} abandoned working director{(result.Removed == 1 ? "y" : "ies")} from earlier sessions.");
            }

            foreach (var error in result.Errors)
            {
                output.Write(OutputSeverity.Warning, "Housekeeping", error);
            }
        }
        catch (Exception ex)
        {
            output.Write(OutputSeverity.Warning, "Housekeeping", "Cleanup of earlier sessions failed: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Remove the app-owned extraction directory; never touches the original bundle.
        _services?.GetService<WorkspaceViewModel>()?.Close();
        _services?.Dispose();
        _theme?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Ingestion and parsing. Parser order is priority order; the unsupported fallback goes last.
        services.AddSingleton<IArchiveProvider, ZipArchiveProvider>();
        services.AddSingleton<IArchiveProvider, CabArchiveProvider>();
        services.AddSingleton<IBundleIngestor, BundleIngestor>();
        services.AddSingleton<IEventMessageFormatter, ProviderMessageFormatter>();
        services.AddSingleton<IDiagnosticParser, RegFileParser>();
        services.AddSingleton<IDiagnosticParser, EvtxParser>();
        services.AddSingleton<IDiagnosticParser, StructuredFileParser>();
        services.AddSingleton<IDiagnosticParser, TextLogParser>();
        services.AddSingleton<IDiagnosticParser, UnsupportedArtifactParser>();

        // One parse per artifact, shared by the viewers, global search and the rules.
        services.AddSingleton<DocumentLoader>();
        services.AddSingleton(sp => new CachingDocumentLoader(sp.GetRequiredService<DocumentLoader>()));
        services.AddSingleton<IDocumentLoader>(sp => sp.GetRequiredService<CachingDocumentLoader>());
        services.AddSingleton<IDocumentCache>(sp => sp.GetRequiredService<CachingDocumentLoader>());
        services.AddSingleton<INavigationHistory, NavigationHistory>();
        services.AddSingleton<IGlobalSearchService, GlobalSearchService>();
        foreach (var rule in DefaultRules.Create())
        {
            services.AddSingleton(rule);
        }

        services.AddSingleton<IFindingsService, FindingsService>();
        services.AddSingleton<ITimelineService, TimelineService>();

        // Application services.
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<OutputViewModel>();
        services.AddSingleton<IOutputLog>(sp => sp.GetRequiredService<OutputViewModel>());

        // View models and shell.
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<DocumentHostViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<ProblemsViewModel>();
        services.AddSingleton<SearchResultsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
