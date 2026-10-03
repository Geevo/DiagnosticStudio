using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.StructuredViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Parsers.Structured;

namespace DiagnosticStudio.Tests.App;

public sealed class StructuredViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-sviewer-" + Guid.NewGuid().ToString("N"));

    public StructuredViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Json =
        "{\n" +                                           // 1
        "  \"service\": \"agent\",\n" +                   // 2
        "  \"retries\": 3,\n" +                           // 3
        "  \"servers\": [\n" +                            // 4
        "    { \"host\": \"a.example\", \"port\": 443 },\n" + // 5
        "    { \"host\": \"b.example\", \"port\": 8443 }\n" + // 6
        "  ]\n" +                                         // 7
        "}\n";

    private const string Xml =
        "<Config>\n" +                                    // 1
        "  <Name>agent</Name>\n" +                        // 2
        "  <Server host=\"a\" port=\"1\">\n" +            // 3
        "    <Note>first needle</Note>\n" +               // 4
        "  </Server>\n" +                                 // 5
        "  <Server host=\"b\" port=\"2\"/>\n" +           // 6
        "</Config>\n";                                    // 7

    private (DiagnosticArtifact Artifact, StructuredDocument Document) Load(string name, string content, ArtifactType type)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = type,
        };
        var document = (StructuredDocument)new StructuredFileParser().ParseAsync(artifact, CancellationToken.None).GetAwaiter().GetResult();
        return (artifact, document);
    }

    private StructuredViewerViewModel JsonViewer(out DiagnosticArtifact artifact)
    {
        var loaded = Load("a.json", Json, ArtifactType.Json);
        artifact = loaded.Artifact;
        return new StructuredViewerViewModel(loaded.Document);
    }

    private StructuredViewerViewModel XmlViewer(out DiagnosticArtifact artifact)
    {
        var loaded = Load("a.xml", Xml, ArtifactType.Xml);
        artifact = loaded.Artifact;
        return new StructuredViewerViewModel(loaded.Document);
    }

    // ---- tree ----

    [Fact]
    public void The_tree_opens_on_the_root_expanded_and_selected()
    {
        var viewer = JsonViewer(out _);

        var root = Assert.Single(viewer.Roots);
        Assert.True(root.IsExpanded);
        Assert.Same(root, viewer.SelectedNode);
        Assert.Equal("(root)", root.DisplayName);
        Assert.Equal("{3}", root.Summary);
        Assert.Equal("(root)", viewer.SelectedPath);
        Assert.Equal(new[] { "service", "retries", "servers" }, root.Children.Select(c => c.DisplayName));
        Assert.Equal(new[] { "\"agent\"", "3", "[2]" }, root.Children.Select(c => c.Summary));
    }

    [Fact]
    public void Children_are_created_only_when_a_node_is_expanded()
    {
        var viewer = JsonViewer(out _);
        var servers = viewer.Roots[0].Children.Single(c => c.DisplayName == "servers");

        Assert.True(servers.Children.Single().IsPlaceholder);

        servers.IsExpanded = true;

        Assert.Equal(new[] { "[0]", "[1]" }, servers.Children.Select(c => c.DisplayName));
        Assert.All(servers.Children, c => Assert.False(c.IsPlaceholder));
    }

    [Fact]
    public void Xml_shows_the_document_items_as_roots_and_inlines_simple_text()
    {
        var viewer = XmlViewer(out _);

        var config = Assert.Single(viewer.Roots);
        Assert.Equal("Config", config.DisplayName);
        var name = config.Children.First(c => c.DisplayName == "Name");
        Assert.Equal("agent", name.Summary);                     // text shown on the element's own row
        Assert.Empty(name.Children);                             // and no child node for it
        var server = config.Children.First(c => c.DisplayName == "Server");
        server.IsExpanded = true;
        Assert.Equal(new[] { "@host", "@port", "Note" }, server.Children.Select(c => c.DisplayName));
        Assert.Equal("first needle", server.Children.Single(c => c.DisplayName == "Note").Summary);
    }

    [Fact]
    public void Selecting_a_node_shows_its_path_value_and_line()
    {
        var viewer = XmlViewer(out _);
        var server = viewer.Roots[0].Children.First(c => c.DisplayName == "Server");
        server.IsExpanded = true;

        var host = server.Children.First(c => c.DisplayName == "@host");
        host.IsSelected = true;

        Assert.Equal("/Config[1]/Server[1]/@host", viewer.SelectedPath);
        Assert.Equal("a", viewer.DetailText);
        Assert.Contains("Attribute", viewer.DetailInfo);
        Assert.Contains("line 3", viewer.DetailInfo);

        server.IsSelected = true;
        Assert.Contains("host = \"a\"", viewer.DetailText);
        Assert.Contains("port = \"1\"", viewer.DetailText);
    }

    // ---- navigation ----

    [Fact]
    public void A_json_node_location_reveals_and_selects_the_node()
    {
        var viewer = JsonViewer(out var artifact);
        var location = DiagnosticLocation.ForJsonNode(artifact.Id, "/servers/1/port");
        var revealed = new List<StructuredNode>();
        viewer.NodeRevealRequested += (_, node) => revealed.Add(node);

        Assert.True(viewer.NavigateTo(location));

        Assert.Equal("/servers/1/port", viewer.SelectedPath);
        Assert.Equal("8443", viewer.DetailText);
        Assert.Equal(StructuredViewerViewModel.StructureTab, viewer.SelectedTabIndex);
        Assert.Equal("port", revealed.Single().Name);
        // The tree was expanded down to it.
        var servers = viewer.Roots[0].Children.Single(c => c.DisplayName == "servers");
        Assert.True(servers.IsExpanded);
        Assert.True(servers.Children[1].IsExpanded);
    }

    [Fact]
    public void An_xml_node_location_reveals_and_selects_the_node()
    {
        var viewer = XmlViewer(out var artifact);

        Assert.True(viewer.NavigateTo(DiagnosticLocation.ForXmlNode(artifact.Id, "/Config[1]/Server[1]/Note[1]")));

        Assert.Equal("/Config[1]/Server[1]/Note[1]", viewer.SelectedPath);
        Assert.Equal("first needle", viewer.DetailText.Trim());
    }

    [Fact]
    public void A_location_for_text_shown_inline_selects_its_element()
    {
        var viewer = XmlViewer(out var artifact);

        Assert.True(viewer.NavigateTo(DiagnosticLocation.ForXmlNode(artifact.Id, "/Config[1]/Name[1]/text()[1]")));

        Assert.Equal("/Config[1]/Name[1]", viewer.SelectedPath);
    }

    [Fact]
    public void Unresolvable_or_foreign_locations_are_refused()
    {
        var json = JsonViewer(out var artifact);

        Assert.False(json.NavigateTo(DiagnosticLocation.ForJsonNode(artifact.Id, "/servers/9")));
        Assert.False(json.NavigateTo(DiagnosticLocation.ForXmlNode(artifact.Id, "/Config[1]")));   // wrong format
        Assert.False(json.NavigateTo(DiagnosticLocation.ForRegistry(artifact.Id, @"HKLM\Software")));
        Assert.False(json.NavigateTo(DiagnosticLocation.ForArtifact(artifact.Id)));
    }

    [Fact]
    public void A_line_location_opens_the_raw_text_there_and_selects_the_node_on_that_line()
    {
        var viewer = JsonViewer(out var artifact);

        Assert.True(viewer.NavigateTo(DiagnosticLocation.ForLine(artifact.Id, 6)));

        Assert.Equal(StructuredViewerViewModel.RawTab, viewer.SelectedTabIndex);
        Assert.Equal(6, viewer.Raw.CurrentLine);
        Assert.Equal("/servers/1/port", viewer.SelectedPath);   // the last node that starts on line 6
    }

    [Fact]
    public void A_search_hit_line_in_xml_lands_on_the_raw_text_and_its_element()
    {
        var viewer = XmlViewer(out var artifact);

        viewer.NavigateTo(DiagnosticLocation.ForLine(artifact.Id, 4));
        viewer.Highlight(new SearchHighlight("needle", MatchCase: false));

        Assert.Equal(StructuredViewerViewModel.RawTab, viewer.SelectedTabIndex);
        Assert.Equal(4, viewer.Raw.CurrentLine);
        Assert.Equal("/Config[1]/Server[1]/Note[1]", viewer.SelectedPath);
        Assert.Equal("needle", viewer.Raw.FindText);
    }

    [Fact]
    public void Show_in_raw_source_jumps_to_the_selected_nodes_line()
    {
        var viewer = JsonViewer(out var artifact);
        viewer.NavigateTo(DiagnosticLocation.ForJsonNode(artifact.Id, "/servers/0/host"));

        viewer.ShowInRawSourceCommand.Execute(null);

        Assert.Equal(StructuredViewerViewModel.RawTab, viewer.SelectedTabIndex);
        Assert.Equal(5, viewer.Raw.CurrentLine);
    }

    // ---- find ----

    [Fact]
    public async Task Find_selects_matching_nodes_and_cycles_through_them()
    {
        var viewer = JsonViewer(out _);

        viewer.FindText = "example";
        await viewer.PendingSearch;

        Assert.Equal(2, viewer.MatchCount);
        Assert.Equal("/servers/0/host", viewer.SelectedPath);
        Assert.Equal("1 of 2", viewer.SearchStatus);

        viewer.FindNextCommand.Execute(null);
        Assert.Equal("/servers/1/host", viewer.SelectedPath);
        viewer.FindNextCommand.Execute(null);
        Assert.Equal("/servers/0/host", viewer.SelectedPath);   // wraps
        viewer.FindPreviousCommand.Execute(null);
        Assert.Equal("/servers/1/host", viewer.SelectedPath);
    }

    [Fact]
    public async Task Find_matches_names_and_reports_when_nothing_matches()
    {
        var viewer = JsonViewer(out _);

        viewer.FindText = "retries";
        await viewer.PendingSearch;
        Assert.Equal("/retries", viewer.SelectedPath);

        viewer.FindText = "no such thing";
        await viewer.PendingSearch;
        Assert.Equal("No matches", viewer.SearchStatus);
        Assert.Equal(0, viewer.MatchCount);

        viewer.FindText = string.Empty;
        await viewer.PendingSearch;
        Assert.Equal(string.Empty, viewer.SearchStatus);
    }

    [Fact]
    public async Task Find_in_xml_reaches_text_that_is_shown_inline()
    {
        var viewer = XmlViewer(out _);

        viewer.FindText = "needle";
        await viewer.PendingSearch;

        Assert.Equal("/Config[1]/Server[1]/Note[1]", viewer.SelectedPath);   // the element showing the text
    }

    // ---- display ----

    [Fact]
    public void Long_values_are_summarised_on_one_line_and_flagged_in_the_detail()
    {
        var text = new string('x', 50_000);
        var loaded = Load("big.json", "{\"s\": \"" + text + "\", \"multi\": \"a\\nb\"}", ArtifactType.Json);
        var viewer = new StructuredViewerViewModel(loaded.Document);

        var s = viewer.Roots[0].Children[0];
        Assert.True(s.Summary.Length < 300);
        s.IsSelected = true;
        Assert.Contains("first 16,384 of 50,000", viewer.DetailInfo.Replace(" ", " "));
        Assert.Equal(StructuredNode.MaxStoredValueChars, viewer.DetailText.Length);
        Assert.Equal("\"a↵b\"", viewer.Roots[0].Children[1].Summary);
    }

    [Fact]
    public void Json_lines_show_each_top_level_value_as_a_root()
    {
        var loaded = Load("log.json", "{\"n\":1}\n{\"n\":2}\n", ArtifactType.Json);
        var viewer = new StructuredViewerViewModel(loaded.Document);

        Assert.Equal(new[] { "[0]", "[1]" }, viewer.Roots.Select(r => r.DisplayName));
        Assert.True(viewer.NavigateTo(DiagnosticLocation.ForJsonNode(loaded.Artifact.Id, "/1/n")));
        Assert.Equal("/1/n", viewer.SelectedPath);
    }

    // ---- through the document host ----

    private sealed class FixedIngestor : IBundleIngestor
    {
        private readonly InvestigationWorkspace _workspace;

        public FixedIngestor(InvestigationWorkspace workspace) => _workspace = workspace;

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(_workspace);
    }

    [Fact]
    public async Task The_document_host_opens_xml_with_the_structured_viewer_and_resolves_every_location_kind()
    {
        var (artifact, _) = Load("a.xml", Xml, ArtifactType.Xml);
        var workspace = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { artifact }, Array.Empty<IngestionIssue>());
        var output = new OutputViewModel();
        var workspaceVm = new WorkspaceViewModel(new FixedIngestor(workspace), output);
        var loader = new DocumentLoader(new IDiagnosticParser[] { new StructuredFileParser(), new TextLogParser(), new UnsupportedArtifactParser() });
        var host = new DocumentHostViewModel(workspaceVm, loader, new NavigationHistory(), output);
        await workspaceVm.OpenAsync(_dir);

        host.OpenLocation(DiagnosticLocation.ForXmlNode(artifact.Id, "/Config[1]/Server[2]/@host"));
        var document = host.Documents.OfType<ArtifactDocumentViewModel>().Single();
        for (var i = 0; i < 200 && document.IsLoading; i++)
        {
            await Task.Delay(25);
        }

        var viewer = Assert.IsType<StructuredViewerViewModel>(document.Viewer);
        Assert.Equal("/Config[1]/Server[2]/@host", viewer.SelectedPath);   // the pending location was applied after loading

        host.OpenLocation(DiagnosticLocation.ForLine(artifact.Id, 4), new SearchHighlight("needle", false));
        Assert.Equal(StructuredViewerViewModel.RawTab, viewer.SelectedTabIndex);
        Assert.Equal(4, viewer.Raw.CurrentLine);
    }
}
