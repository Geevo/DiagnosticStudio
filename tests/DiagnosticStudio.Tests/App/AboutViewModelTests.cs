using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels.About;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.App;

public sealed class AboutViewModelTests
{
    private sealed class FakeCleanup : IStaleWorkspaceCleanup
    {
        public WorkspaceSurvey Survey { get; set; } = WorkspaceSurvey.None;
        public WorkspaceSweepResult Sweep { get; set; } = WorkspaceSweepResult.None;
        public WorkspaceSurvey SurveyAfterClear { get; set; } = WorkspaceSurvey.None;
        public int Cleared { get; private set; }

        public Task<WorkspaceSurvey> SurveyAsync() => Task.FromResult(Survey);

        public Task<WorkspaceSweepResult> ClearAsync()
        {
            Cleared++;
            Survey = SurveyAfterClear;
            return Task.FromResult(Sweep);
        }
    }

    private static readonly AppInfo Info = new("Diagnostic Studio", "1.2.3", "https://example.test/repo");

    [Fact]
    public void The_dialog_shows_the_version_and_repository_address()
    {
        var vm = new AboutViewModel(Info, new FakeCleanup());

        Assert.Equal("Version 1.2.3", vm.VersionText);
        Assert.Equal("https://example.test/repo", vm.RepositoryUrl);
    }

    [Fact]
    public async Task With_nothing_left_over_there_is_nothing_to_clear()
    {
        var vm = new AboutViewModel(Info, new FakeCleanup());

        await vm.LoadAsync();

        Assert.False(vm.HasStale);
        Assert.False(vm.ClearStaleCommand.CanExecute(null));
        Assert.Equal("No leftover extracted archives.", vm.CleanupText);
    }

    [Fact]
    public async Task Leftovers_are_counted_and_can_be_cleared()
    {
        var cleanup = new FakeCleanup
        {
            Survey = new WorkspaceSurvey(3, 5 * 1024 * 1024),
            Sweep = new WorkspaceSweepResult(3, 0, 0, Array.Empty<string>()),
        };
        var vm = new AboutViewModel(Info, cleanup);

        await vm.LoadAsync();
        Assert.True(vm.HasStale);
        Assert.True(vm.ClearStaleCommand.CanExecute(null));
        Assert.Contains("3", vm.CleanupText);
        Assert.Contains("5 MB", vm.CleanupText);

        await vm.ClearStaleCommand.ExecuteAsync(null);

        Assert.Equal(1, cleanup.Cleared);
        Assert.False(vm.HasStale);
        Assert.False(vm.ClearStaleCommand.CanExecute(null));
        Assert.Contains("Removed 3", vm.CleanupText);
    }

    [Fact]
    public async Task What_could_not_be_removed_is_said_and_stays_clearable()
    {
        var cleanup = new FakeCleanup
        {
            Survey = new WorkspaceSurvey(2, 10),
            SurveyAfterClear = new WorkspaceSurvey(1, 5),
            Sweep = new WorkspaceSweepResult(1, 0, 1, new[] { "Could not remove X" }),
        };
        var vm = new AboutViewModel(Info, cleanup);
        await vm.LoadAsync();

        await vm.ClearStaleCommand.ExecuteAsync(null);

        Assert.Contains("1 could not be removed", vm.CleanupText);
        Assert.Contains("Could not remove X", vm.CleanupText);
        Assert.True(vm.ClearStaleCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void Sizes_are_shown_in_the_largest_whole_unit(long bytes, string expected)
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal(expected, AboutViewModel.SizeText(bytes));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _before = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _before;
    }

    [Fact]
    public void App_info_reads_the_release_number_without_the_revision_and_the_repository_from_the_build()
    {
        var info = AppInfo.FromAssembly(typeof(AppInfo).Assembly);

        Assert.DoesNotContain('+', info.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", info.Version);
        Assert.StartsWith("https://", info.RepositoryUrl);
    }
}
