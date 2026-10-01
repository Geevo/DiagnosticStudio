using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DiagnosticStudio.App;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.Views;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Tests.Evtx;
using Microsoft.Extensions.DependencyInjection;

namespace DiagnosticStudio.Tests.App;

/// <summary>
/// Loads the real application window with the real services and opens one of every kind of artifact plus the
/// timeline. The view-model tests cannot see a XAML file that fails to load or a template that does not resolve, and
/// either one takes the whole application down at startup.
/// </summary>
public sealed class UiSmokeTests
{
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                lock (Messages)
                {
                    Messages.Add(message);
                }
            }
        }
    }

    private static void RunOnSta(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            // A view that fails to load throws from inside the dispatcher; report it as this test's failure.
            dispatcher.UnhandledException += (_, e) =>
            {
                error ??= e.Exception;
                e.Handled = true;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90)))
        {
            throw new TimeoutException("The UI smoke test did not finish.");
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    private static T? FindVisual<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static async Task WaitLoaded(ArtifactDocumentViewModel document)
    {
        for (var i = 0; i < 400 && document.IsLoading; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(document.IsLoading, document.Title + " did not finish loading");
    }

    private static string MakeBundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ds-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllLines(
            Path.Combine(dir, "agent.log"),
            new[] { "2026-07-23 14:12:30 INFO start", "2026-07-23 14:13:30 ERROR boom happened", "    at Foo.Bar()", "2026-07-23 14:20:00 WARN slow" });
        File.WriteAllText(Path.Combine(dir, "policy.reg"), "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Contoso]\r\n\"Mode\"=\"boom\"\r\n");
        File.WriteAllText(Path.Combine(dir, "config.xml"), "<?xml version=\"1.0\"?>\r\n<Config><Item id=\"1\">boom</Item></Config>\r\n");
        File.WriteAllText(Path.Combine(dir, "data.json"), "{\"items\":[{\"name\":\"boom\"}]}\r\n");
        var events = Enumerable.Range(0, 5)
            .Select(i => new TestEvent(500 + i, "Provider", (uint)(100 + i), EventLevels.Error, new DateTime(2026, 7, 23, 14, 13, 0, DateTimeKind.Utc).AddSeconds(i * 20), "PC", "a" + i, "b"))
            .ToList();
        File.WriteAllBytes(Path.Combine(dir, "System.evtx"), new EvtxBuilder().Build(events));
        return dir;
    }

    [Fact]
    public void The_window_and_every_viewer_load_with_the_real_services()
    {
        var dir = MakeBundle();
        var errors = new BindingErrors();
        try
        {
            RunOnSta(async () =>
            {
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

                // What App.xaml sets up, without App.OnStartup (which also sweeps the machine's temp folder).
                var app = new Application { ThemeMode = ThemeMode.System };
                foreach (var dictionary in new[] { "Theme", "Templates" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/DiagnosticStudio.App;component/Views/{dictionary}.xaml"),
                    });
                }

                using var theme = new ThemeService(app);
                theme.Start();

                var configure = typeof(DiagnosticStudio.App.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
                using var services = (ServiceProvider)configure.Invoke(null, null)!;
                var window = services.GetRequiredService<MainWindow>();
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = 0;
                window.Show();
                await Settle();

                var main = services.GetRequiredService<MainWindowViewModel>();
                await main.Workspace.OpenAsync(dir);
                await Settle();
                Assert.Equal(5, main.Workspace.Current!.Artifacts.Count);

                // One of each kind of viewer.
                var expected = new Dictionary<string, Type>
                {
                    ["agent.log"] = typeof(TextViewerView),
                    ["policy.reg"] = typeof(RegistryViewerView),
                    ["config.xml"] = typeof(StructuredViewerView),
                    ["data.json"] = typeof(StructuredViewerView),
                    ["System.evtx"] = typeof(EventLogViewerView),
                };
                foreach (var artifact in main.Workspace.Current.Artifacts)
                {
                    main.Documents.OpenArtifact(artifact);
                    var document = (ArtifactDocumentViewModel)main.Documents.ActiveDocument!;
                    await WaitLoaded(document);
                    await Settle();
                    Assert.NotNull(FindVisual(window, expected[artifact.Name]));
                }

                // The timeline, with everything it can show.
                var timeline = main.Documents.ShowTimeline()!;
                await timeline.PendingBuild;
                await Settle();
                var view = FindVisual<TimelineView>(window);
                Assert.NotNull(view);
                Assert.Equal(2, timeline.Sources.Count);
                Assert.Equal(8, timeline.Rows.Count); // 3 timestamped log lines + 5 events

                // A search puts hits in the results tree.
                main.SearchResults.Query = "boom";
                main.SearchResults.SearchCommand.Execute(null);
                await main.SearchResults.PendingSearch;
                await Settle();
                Assert.True(main.SearchResults.Results.Count > 0);

                // Findings view, overview and the other panels are part of the same window.
                main.SelectedBottomTabIndex = (int)BottomPanelTab.Problems;
                await Settle();
                main.SelectedBottomTabIndex = (int)BottomPanelTab.Output;
                await Settle();
                main.Documents.ShowOverview();
                await Settle();

                window.Close();
                main.Workspace.Close();
            });

            var timelineErrors = errors.Messages.Where(m => m.Contains("Timeline", StringComparison.Ordinal)).ToList();
            Assert.True(timelineErrors.Count == 0, "Binding errors in the timeline:\n" + string.Join("\n", timelineErrors));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DependencyObject? FindVisual(DependencyObject root, Type type)
    {
        if (type.IsInstanceOfType(root))
        {
            return root;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindVisual(VisualTreeHelper.GetChild(root, i), type) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
