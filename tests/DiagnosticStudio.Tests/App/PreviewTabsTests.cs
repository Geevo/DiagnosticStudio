using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

/// <summary>A single click previews a file in a tab the next preview replaces; a double-click or the pin keeps it.</summary>
public sealed class PreviewTabsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-prev-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly Ingestor _ingestor = new();
    private readonly TokenLoader _loader = new();
    private readonly DiagnosticArtifact _a = Artifact("a.log", ArtifactType.TextLog);
    private readonly DiagnosticArtifact _b = Artifact("b.log", ArtifactType.TextLog);
    private readonly DiagnosticArtifact _c = Artifact("c.log", ArtifactType.TextLog);

    public PreviewTabsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Ingestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    /// <summary>Loads never finish; remembers the token each load was given so tests can see it cancelled.</summary>
    private sealed class TokenLoader : IDocumentLoader
    {
        public Dictionary<Guid, CancellationToken> Tokens { get; } = new();

        public Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            lock (Tokens)
            {
                Tokens[artifact.Id] = cancellationToken;
            }

            var tcs = new TaskCompletionSource<DocumentLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    private async Task<(DocumentHostViewModel Host, ExplorerViewModel Explorer)> Create()
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, _loader, new NavigationHistory(), _output);
        var explorer = new ExplorerViewModel(workspace, host);
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _a, _b, _c }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
        return (host, explorer);
    }

    private static ArtifactDocumentViewModel Tab(DocumentHostViewModel host, DiagnosticArtifact artifact) =>
        host.Documents.OfType<ArtifactDocumentViewModel>().Single(d => d.Artifact.Id == artifact.Id);

    // ---- opening ----

    [Fact]
    public async Task A_preview_is_marked_and_a_normal_open_is_not()
    {
        var (host, _) = await Create();

        host.OpenArtifact(_a, preview: true);
        host.OpenArtifact(_b);

        Assert.True(Tab(host, _a).IsPreview);
        Assert.False(Tab(host, _b).IsPreview);
    }

    [Fact]
    public async Task Previewing_another_file_swaps_the_preview_tab_and_keeps_the_count()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a, preview: true);
        var count = host.Documents.Count;
        var position = host.Documents.IndexOf(Tab(host, _a));

        host.OpenArtifact(_b, preview: true);

        Assert.Equal(count, host.Documents.Count);
        Assert.DoesNotContain(host.Documents.OfType<ArtifactDocumentViewModel>(), d => d.Artifact.Id == _a.Id);
        Assert.Equal(position, host.Documents.IndexOf(Tab(host, _b)));
        Assert.Same(Tab(host, _b), host.ActiveDocument);
    }

    [Fact]
    public async Task Kept_tabs_are_never_replaced_by_a_preview()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a);
        host.OpenArtifact(_b, preview: true);
        host.OpenArtifact(_c, preview: true);

        Assert.Contains(Tab(host, _a), host.Documents);
        Assert.DoesNotContain(host.Documents.OfType<ArtifactDocumentViewModel>(), d => d.Artifact.Id == _b.Id);
        Assert.True(Tab(host, _c).IsPreview);
        Assert.Equal(3, host.Documents.Count); // overview, a, c
    }

    [Fact]
    public async Task Previewing_a_file_that_is_already_kept_just_shows_it()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a);
        host.OpenArtifact(_b);

        host.OpenArtifact(_a, preview: true);

        Assert.Same(Tab(host, _a), host.ActiveDocument);
        Assert.False(Tab(host, _a).IsPreview);
        Assert.Equal(3, host.Documents.Count);
    }

    [Fact]
    public async Task Opening_the_previewed_file_for_real_keeps_its_tab()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a, preview: true);
        var tab = Tab(host, _a);

        host.OpenArtifact(_a);

        Assert.Same(tab, Tab(host, _a));
        Assert.False(tab.IsPreview);
        host.OpenArtifact(_b, preview: true);
        Assert.Contains(tab, host.Documents); // no longer replaced
    }

    [Fact]
    public async Task The_pin_keeps_a_preview()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a, preview: true);

        host.PinCommand.Execute(Tab(host, _a));
        host.OpenArtifact(_b, preview: true);

        Assert.Contains(Tab(host, _a), host.Documents);
        Assert.False(Tab(host, _a).IsPreview);
        Assert.True(Tab(host, _b).IsPreview);
        host.PinCommand.Execute(null); // nothing happens
    }

    [Fact]
    public async Task Following_a_link_to_a_file_that_is_in_the_preview_tab_keeps_that_tab()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a, preview: true);

        host.OpenLocation(DiagnosticLocation.ForLine(_a.Id, 5));

        Assert.False(Tab(host, _a).IsPreview);
    }

    [Fact]
    public async Task Close_all_closes_a_preview_too()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a, preview: true);

        host.CloseAllCommand.Execute(null);

        Assert.Single(host.Documents);
    }

    // ---- stopping the work for a tab that is gone ----

    [Fact]
    public async Task A_replaced_preview_stops_loading_its_file()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a, preview: true);
        var token = _loader.Tokens[_a.Id];
        Assert.False(token.IsCancellationRequested);

        host.OpenArtifact(_b, preview: true);

        Assert.True(token.IsCancellationRequested);
        Assert.False(_loader.Tokens[_b.Id].IsCancellationRequested);
    }

    [Fact]
    public async Task Closing_a_tab_stops_loading_its_file()
    {
        var (host, _) = await Create();
        host.OpenArtifact(_a);
        var token = _loader.Tokens[_a.Id];

        host.CloseCommand.Execute(Tab(host, _a));

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Opening_another_bundle_stops_the_loads_of_the_old_tabs()
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, _loader, new NavigationHistory(), _output);
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _a }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
        host.OpenArtifact(_a);
        var token = _loader.Tokens[_a.Id];

        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { _b }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);

        Assert.True(token.IsCancellationRequested);
    }

    // ---- from the explorer ----

    private static ExplorerNodeViewModel Node(ExplorerViewModel explorer, string name) =>
        All(explorer.Nodes).Single(n => n.Name == name);

    private static IEnumerable<ExplorerNodeViewModel> All(IEnumerable<ExplorerNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));

    [Fact]
    public async Task A_click_previews_the_file_at_once()
    {
        var (host, explorer) = await Create();

        explorer.PreviewNodeCommand.Execute(Node(explorer, "a.log"));

        Assert.True(Tab(host, _a).IsPreview);
        Assert.Same(Tab(host, _a), host.ActiveDocument);
    }

    [Fact]
    public async Task Clicking_a_folder_opens_nothing()
    {
        var (host, explorer) = await Create();
        var before = host.Documents.Count;

        explorer.PreviewNodeCommand.Execute(explorer.Nodes[0]);

        Assert.Equal(before, host.Documents.Count);
    }

    [Fact]
    public async Task Selecting_a_file_previews_it_after_a_short_pause()
    {
        var (host, explorer) = await Create();

        Node(explorer, "a.log").IsSelected = true;
        Assert.DoesNotContain(host.Documents.OfType<ArtifactDocumentViewModel>(), d => d.Artifact.Id == _a.Id);

        await explorer.PendingPreview;

        Assert.True(Tab(host, _a).IsPreview);
    }

    [Fact]
    public async Task Moving_the_selection_quickly_previews_only_where_it_comes_to_rest()
    {
        var (host, explorer) = await Create();

        Node(explorer, "a.log").IsSelected = true;
        Node(explorer, "b.log").IsSelected = true;
        Node(explorer, "c.log").IsSelected = true;
        await explorer.PendingPreview;

        var opened = host.Documents.OfType<ArtifactDocumentViewModel>().Select(d => d.Artifact.Name).ToList();
        Assert.Equal(new[] { "c.log" }, opened);
        Assert.DoesNotContain(_loader.Tokens.Keys, id => id == _a.Id || id == _b.Id); // the files passed over were never loaded
    }

    [Fact]
    public async Task Double_clicking_keeps_the_file_and_cancels_a_pending_preview_of_another()
    {
        var (host, explorer) = await Create();
        Node(explorer, "a.log").IsSelected = true; // starts waiting to preview a

        explorer.OpenNodeCommand.Execute(Node(explorer, "b.log"));
        await explorer.PendingPreview;

        Assert.False(Tab(host, _b).IsPreview);
        Assert.DoesNotContain(host.Documents.OfType<ArtifactDocumentViewModel>(), d => d.Artifact.Id == _a.Id);
    }

    [Fact]
    public async Task Double_clicking_the_file_in_the_preview_tab_keeps_it()
    {
        var (host, explorer) = await Create();
        explorer.PreviewNodeCommand.Execute(Node(explorer, "a.log"));

        explorer.OpenNodeCommand.Execute(Node(explorer, "a.log"));

        Assert.False(Tab(host, _a).IsPreview);
        Assert.Equal(2, host.Documents.Count);
    }

    [Fact]
    public async Task Clicking_a_file_that_is_already_kept_does_not_open_a_second_tab()
    {
        var (host, explorer) = await Create();
        explorer.OpenNodeCommand.Execute(Node(explorer, "a.log"));

        explorer.PreviewNodeCommand.Execute(Node(explorer, "a.log"));

        Assert.Equal(2, host.Documents.Count);
        Assert.False(Tab(host, _a).IsPreview);
    }
}
