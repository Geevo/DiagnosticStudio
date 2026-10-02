using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public sealed class ExplorerViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-exp-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly Ingestor _ingestor = new();

    public ExplorerViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Ingestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private static DiagnosticArtifact At(ArtifactType type, params string[] provenance) => new()
    {
        Id = Guid.NewGuid(),
        Name = provenance[^1],
        OriginalPath = provenance[^1],
        Provenance = provenance,
        ArtifactType = type,
    };

    private static DiagnosticArtifact[] Sample() => new[]
    {
        At(ArtifactType.TextLog, "Bundle.zip", "Logs", "agent.log"),
        At(ArtifactType.TextLog, "Bundle.zip", "Logs", "setup.log"),
        At(ArtifactType.RegistryExport, "Bundle.zip", "Registry", "policy.reg"),
        At(ArtifactType.EventLog, "Bundle.zip", "mdm.cab", "Events", "System.evtx"),
    };

    private async Task<ExplorerViewModel> Open(params DiagnosticArtifact[] artifacts)
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, new FakeLoader(), new NavigationHistory(), _output);
        var explorer = new ExplorerViewModel(workspace, host);
        await OpenInto(workspace, artifacts);
        return explorer;
    }

    private async Task OpenInto(WorkspaceViewModel workspace, params DiagnosticArtifact[] artifacts)
    {
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, artifacts, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
    }

    private static IEnumerable<ExplorerNodeViewModel> All(IEnumerable<ExplorerNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));

    private static ExplorerNodeViewModel Node(ExplorerViewModel explorer, string name) =>
        All(explorer.Nodes).Single(n => n.Name == name);

    private static string[] FileNames(ExplorerViewModel explorer) =>
        All(explorer.Nodes).Where(n => n.Artifact is not null).Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---- expand / collapse ----

    [Fact]
    public async Task The_tree_opens_with_only_the_top_level_expanded()
    {
        var explorer = await Open(Sample());

        var root = Assert.Single(explorer.Nodes);
        Assert.True(root.IsExpanded);
        Assert.All(root.Children, c => Assert.False(c.IsExpanded));
    }

    [Fact]
    public async Task Expand_all_opens_every_folder_and_archive()
    {
        var explorer = await Open(Sample());

        explorer.ExpandAllCommand.Execute(null);

        Assert.All(All(explorer.Nodes).Where(n => n.Children.Count > 0), n => Assert.True(n.IsExpanded, n.Name));
    }

    [Fact]
    public async Task Collapse_all_closes_everything_including_the_top()
    {
        var explorer = await Open(Sample());
        explorer.ExpandAllCommand.Execute(null);

        explorer.CollapseAllCommand.Execute(null);

        Assert.All(All(explorer.Nodes), n => Assert.False(n.IsExpanded, n.Name));
    }

    // ---- filtering ----

    [Fact]
    public async Task A_filter_keeps_matching_files_and_the_folders_that_lead_to_them()
    {
        var explorer = await Open(Sample());

        explorer.FilterText = "agent";
        await explorer.PendingFilter;

        Assert.Equal(new[] { "agent.log" }, FileNames(explorer));
        Assert.Equal(new[] { "Bundle.zip", "Logs", "agent.log" }, All(explorer.Nodes).Select(n => n.Name));
        Assert.All(All(explorer.Nodes).Where(n => n.Children.Count > 0), n => Assert.True(n.IsExpanded));
        Assert.Equal("1 file matches", explorer.FilterStatus);
    }

    [Fact]
    public async Task The_filter_ignores_case_and_surrounding_spaces()
    {
        var explorer = await Open(Sample());

        explorer.FilterText = "  SYSTEM.EVTX ";
        await explorer.PendingFilter;

        Assert.Equal(new[] { "System.evtx" }, FileNames(explorer));
    }

    [Fact]
    public async Task A_folder_whose_name_matches_stays_with_everything_in_it()
    {
        var explorer = await Open(Sample());

        explorer.FilterText = "logs";
        await explorer.PendingFilter;

        Assert.Equal(new[] { "agent.log", "setup.log" }, FileNames(explorer));
        Assert.Equal("2 files match", explorer.FilterStatus);
        Assert.True(Node(explorer, "Logs").IsExpanded); // opened so the files in it are in view
    }

    [Fact]
    public async Task A_filter_matches_files_inside_nested_archives()
    {
        var explorer = await Open(Sample());

        explorer.FilterText = ".evtx";
        await explorer.PendingFilter;

        Assert.Equal(new[] { "Bundle.zip", "mdm.cab", "Events", "System.evtx" }, All(explorer.Nodes).Select(n => n.Name));
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_says_so()
    {
        var explorer = await Open(Sample());

        explorer.FilterText = "zzz";
        await explorer.PendingFilter;

        Assert.Empty(explorer.Nodes);
        Assert.Equal("No files match", explorer.FilterStatus);
    }

    [Fact]
    public async Task Clearing_the_filter_brings_back_the_whole_tree_at_once()
    {
        var explorer = await Open(Sample());
        explorer.FilterText = "agent";
        await explorer.PendingFilter;

        explorer.ClearFilterCommand.Execute(null);

        Assert.Equal(4, FileNames(explorer).Length);
        Assert.Equal(string.Empty, explorer.FilterStatus);
    }

    [Fact]
    public async Task Typing_is_debounced_so_only_the_last_text_is_applied()
    {
        var explorer = await Open(Sample());
        var rebuilds = 0;
        explorer.Nodes.CollectionChanged += (_, _) => rebuilds++;

        explorer.FilterText = "a";
        explorer.FilterText = "ag";
        explorer.FilterText = "agent";
        await explorer.PendingFilter;

        Assert.Equal(new[] { "agent.log" }, FileNames(explorer));
        Assert.True(rebuilds <= 4, "rebuilt " + rebuilds + " times");
    }

    [Fact]
    public async Task Switching_between_files_and_logical_keeps_the_filter()
    {
        var explorer = await Open(
            At(ArtifactType.TextLog, "Bundle.zip", "agent.log") with { Category = "Intune" },
            At(ArtifactType.TextLog, "Bundle.zip", "other.log"));
        explorer.FilterText = "agent";
        await explorer.PendingFilter;

        explorer.ShowLogicalCommand.Execute(null);

        Assert.Equal(new[] { "agent.log" }, FileNames(explorer));
    }

    [Fact]
    public async Task Opening_another_bundle_clears_the_filter()
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, new FakeLoader(), new NavigationHistory(), _output);
        var explorer = new ExplorerViewModel(workspace, host);
        await OpenInto(workspace, Sample());
        explorer.FilterText = "agent";
        await explorer.PendingFilter;

        await OpenInto(workspace, Sample());

        Assert.Equal(string.Empty, explorer.FilterText);
        Assert.Equal(4, FileNames(explorer).Length);
    }
}
