using System.Text;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.StructuredViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers.Structured;

namespace DiagnosticStudio.Tests.App;

/// <summary>The Raw tab of an XML or JSON file can show an indented copy of a file written on one line.</summary>
public sealed class StructuredViewerPrettyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-pretty-" + Guid.NewGuid().ToString("N"));

    public StructuredViewerPrettyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<StructuredViewerViewModel> Open(string name, string text, ArtifactType type)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = type,
        };
        var document = (StructuredDocument)await new StructuredFileParser().ParseAsync(artifact, CancellationToken.None);
        return new StructuredViewerViewModel(document);
    }

    private static string MinifiedJson(int items)
    {
        var sb = new StringBuilder("{\"items\":[");
        for (var i = 0; i < items; i++)
        {
            sb.Append(i == 0 ? string.Empty : ",").Append("{\"id\":").Append(i).Append(",\"name\":\"item-number-").Append(i).Append("\",\"description\":\"some descriptive text to make the line long\"}");
        }

        return sb.Append("]}").ToString();
    }

    private static string LineText(TextViewerViewModelAccess access, int line) => access.Line(line);

    private readonly record struct TextViewerViewModelAccess(DiagnosticStudio.App.ViewModels.TextViewer.TextViewerViewModel Viewer)
    {
        public string Line(int number) => Viewer.Lines[number - 1].Text;
    }

    // ---- when it is on ----

    [Fact]
    public async Task A_file_written_on_one_line_opens_with_the_indented_copy_on()
    {
        var vm = await Open("one.json", MinifiedJson(40), ArtifactType.Json);

        Assert.True(vm.PrettyRaw);
        await vm.PendingFormat;

        Assert.NotSame(vm.Raw, vm.ActiveRaw);
        Assert.Single(vm.Raw.Lines); // the original is untouched
        Assert.True(vm.ActiveRaw.Lines.Count > 100);
        Assert.StartsWith("Indented copy", vm.PrettyStatus);
        Assert.True(vm.PrettyAvailable);
    }

    [Fact]
    public async Task A_file_that_is_already_indented_opens_as_it_was_collected()
    {
        var text = "{\n  \"a\": 1,\n  \"b\": [\n    2,\n    3\n  ]\n}\n";
        var vm = await Open("pretty.json", text, ArtifactType.Json);

        Assert.False(vm.PrettyRaw);
        Assert.Same(vm.Raw, vm.ActiveRaw);
    }

    [Fact]
    public async Task One_record_per_line_json_lines_are_not_re_indented_by_default()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 200).Select(i => "{\"id\":" + i + ",\"name\":\"" + new string('x', 200) + "\"}"));
        var vm = await Open("log.jsonl", lines, ArtifactType.Json);

        Assert.False(vm.PrettyRaw);
    }

    [Fact]
    public void The_dense_file_test_needs_long_lines_and_many_nodes_on_them()
    {
        static StructuredDocument Doc(int lines, long bytes, int nodes) => new()
        {
            Artifact = new DiagnosticArtifact
            {
                Id = Guid.NewGuid(),
                Name = "x.json",
                OriginalPath = "x.json",
                Provenance = new[] { "x.json" },
                ArtifactType = ArtifactType.Json,
            },
            Format = StructuredFormat.Json,
            Root = new StructuredNode(StructuredNodeKind.Object, null, null, 0, 1, null),
            RawSource = new Sized(lines, bytes),
            NodeCount = nodes,
        };

        Assert.True(StructuredViewerViewModel.LooksMinified(Doc(1, 11_000_000, 900_000)));
        Assert.False(StructuredViewerViewModel.LooksMinified(Doc(30_000, 1_000_000, 100_000)));    // normal indentation
        Assert.False(StructuredViewerViewModel.LooksMinified(Doc(100, 100_000, 800)));             // long lines, few nodes (one big string)
        Assert.False(StructuredViewerViewModel.LooksMinified(Doc(0, 0, 0)));
    }

    private sealed class Sized : ITextLineSource
    {
        public Sized(int lines, long bytes)
        {
            LineCount = lines;
            ByteLength = bytes;
        }

        public int LineCount { get; }
        public long ByteLength { get; }
        public string EncodingName => "test";
        public IReadOnlyList<string> ReadLines(int startLine, int count) => Array.Empty<string>();
        public IEnumerable<string> EnumerateLines(int startLine = 0) => Array.Empty<string>();
    }

    // ---- toggling ----

    [Fact]
    public async Task Turning_it_off_shows_the_original_and_turning_it_on_again_reuses_the_copy()
    {
        var vm = await Open("t.json", MinifiedJson(40), ArtifactType.Json);
        await vm.PendingFormat;
        var formatted = vm.ActiveRaw;

        vm.PrettyRaw = false;
        Assert.Same(vm.Raw, vm.ActiveRaw);

        vm.PrettyRaw = true;
        Assert.Same(formatted, vm.ActiveRaw);
    }

    [Fact]
    public async Task A_file_can_be_indented_on_request_even_when_it_opened_as_collected()
    {
        var vm = await Open("small.json", "{\"a\":1,\"b\":{\"c\":2}}\n", ArtifactType.Json);
        Assert.False(vm.PrettyRaw);

        vm.PrettyRaw = true;
        await vm.PendingFormat;

        Assert.NotSame(vm.Raw, vm.ActiveRaw);
        Assert.Equal(6, vm.ActiveRaw.Lines.Count);
    }

    [Fact]
    public async Task The_active_text_changes_are_announced_so_the_view_swaps_content()
    {
        var vm = await Open("t.json", MinifiedJson(40), ArtifactType.Json);
        await vm.PendingFormat;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PrettyRaw = false;
        vm.PrettyRaw = true;

        Assert.Equal(2, changed.Count(n => n == nameof(StructuredViewerViewModel.ActiveRaw)));
    }

    // ---- links between the tree and the text ----

    [Fact]
    public async Task Show_in_raw_source_goes_to_the_nodes_line_in_the_indented_copy()
    {
        var vm = await Open("m.json", MinifiedJson(40), ArtifactType.Json);
        await vm.PendingFormat;
        vm.Reveal(vm.Document.FindNode("/items/7/name")!);

        vm.ShowInRawSourceCommand.Execute(null);

        Assert.Equal(StructuredViewerViewModel.RawTab, vm.SelectedTabIndex);
        var line = vm.ActiveRaw.CurrentLine;
        Assert.True(line > 1);
        Assert.Contains("\"name\": \"item-number-7\"", new TextViewerViewModelAccess(vm.ActiveRaw).Line(line));
    }

    [Fact]
    public async Task Show_in_raw_source_without_the_copy_uses_the_original_line()
    {
        var vm = await Open("m.json", MinifiedJson(40), ArtifactType.Json);
        await vm.PendingFormat;
        vm.PrettyRaw = false;
        vm.Reveal(vm.Document.FindNode("/items/7/name")!);

        vm.ShowInRawSourceCommand.Execute(null);

        Assert.Same(vm.Raw, vm.ActiveRaw);
        Assert.Equal(1, vm.Raw.CurrentLine);
    }

    [Fact]
    public async Task Switching_to_the_copy_keeps_the_raw_tab_on_the_selected_node()
    {
        var vm = await Open("m.json", MinifiedJson(40), ArtifactType.Json);
        await vm.PendingFormat;
        vm.PrettyRaw = false;
        vm.Reveal(vm.Document.FindNode("/items/9/id")!);
        vm.SelectedTabIndex = StructuredViewerViewModel.RawTab;

        vm.PrettyRaw = true;

        var line = vm.ActiveRaw.CurrentLine;
        Assert.Contains("\"id\": 9", new TextViewerViewModelAccess(vm.ActiveRaw).Line(line));
    }

    [Fact]
    public async Task A_search_highlight_shows_in_both_copies()
    {
        var vm = await Open("m.json", MinifiedJson(40), ArtifactType.Json);
        vm.Highlight(new SearchHighlight("descriptive", MatchCase: false)); // before the copy exists
        await vm.PendingFormat;

        Assert.Equal("descriptive", vm.Raw.FindText);
        Assert.Equal("descriptive", vm.ActiveRaw.FindText);

        vm.Highlight(new SearchHighlight("item-number", MatchCase: false)); // after
        Assert.Equal("item-number", vm.ActiveRaw.FindText);
        Assert.Equal("item-number", vm.Raw.FindText);
    }

    [Fact]
    public async Task A_line_link_into_the_original_goes_to_that_node_in_the_copy()
    {
        // Multi-line file with a long dense line in the middle.
        var text = "{\n  \"before\": 1,\n  \"dense\": " + MinifiedJson(30) + ",\n  \"after\": 2\n}\n";
        var vm = await Open("mixed.json", text, ArtifactType.Json);
        vm.PrettyRaw = true;
        await vm.PendingFormat;

        // Line 3 of the file holds the dense value; a search hit there is a hit on its node.
        vm.NavigateTo(DiagnosticStudio.Core.Navigation.DiagnosticLocation.ForLine(vm.Document.Artifact.Id, 3));

        Assert.Equal(StructuredViewerViewModel.RawTab, vm.SelectedTabIndex);
        Assert.NotNull(vm.SelectedNode);
        Assert.True(vm.ActiveRaw.CurrentLine > 3);
    }

    // ---- failure ----

    [Fact]
    public async Task A_file_that_disappears_cannot_be_indented_and_the_original_stays()
    {
        var vm = await Open("gone.json", "{\"a\":1}", ArtifactType.Json);
        File.Delete(vm.Document.Artifact.ExtractedPath!);

        vm.PrettyRaw = true;
        await vm.PendingFormat;

        Assert.False(vm.PrettyRaw);
        Assert.False(vm.PrettyAvailable);
        Assert.StartsWith("This file cannot be indented", vm.PrettyStatus);
        Assert.Same(vm.Raw, vm.ActiveRaw);
    }

    // ---- XML ----

    [Fact]
    public async Task Xml_written_on_one_line_is_indented_too()
    {
        var items = string.Concat(Enumerable.Range(0, 60).Select(i => "<Item id=\"" + i + "\" name=\"item-number-" + i + "\"><Value>some text to make it long enough</Value></Item>"));
        var vm = await Open("one.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Config>" + items + "</Config>", ArtifactType.Xml);

        Assert.True(vm.PrettyRaw);
        await vm.PendingFormat;

        Assert.True(vm.ActiveRaw.Lines.Count > 100);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", new TextViewerViewModelAccess(vm.ActiveRaw).Line(1));
        vm.Reveal(vm.Document.FindNode("/Config[1]/Item[5]/@name")!);
        vm.ShowInRawSourceCommand.Execute(null);
        Assert.Contains("name=\"item-number-4\"", new TextViewerViewModelAccess(vm.ActiveRaw).Line(vm.ActiveRaw.CurrentLine));
    }
}
