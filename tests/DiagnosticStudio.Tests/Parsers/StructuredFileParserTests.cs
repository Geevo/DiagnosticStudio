using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Parsers.Structured;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.Tests.Parsers;

public sealed class StructuredFileParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-struct-" + Guid.NewGuid().ToString("N"));

    public StructuredFileParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DiagnosticArtifact Write(string name, string content, ArtifactType type)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { name },
            ArtifactType = type,
            Size = new FileInfo(path).Length,
        };
    }

    private static DocumentLoader Loader() => new(new IDiagnosticParser[]
    {
        new StructuredFileParser(), new TextLogParser(), new UnsupportedArtifactParser(),
    });

    [Fact]
    public async Task Xml_and_json_open_as_a_tree_together_with_the_raw_text()
    {
        var xml = Write("a.xml", "<a>\n  <b id=\"1\">x</b>\n</a>\n", ArtifactType.Xml);
        var json = Write("a.json", "{\n  \"k\": [1, 2]\n}\n", ArtifactType.Json);

        var xmlDoc = Assert.IsType<StructuredDocument>((await Loader().LoadAsync(xml, CancellationToken.None)).Document);
        var jsonDoc = Assert.IsType<StructuredDocument>((await Loader().LoadAsync(json, CancellationToken.None)).Document);

        Assert.Equal(StructuredFormat.Xml, xmlDoc.Format);
        Assert.Equal(StructuredFormat.Json, jsonDoc.Format);
        Assert.Equal(4, xmlDoc.NodeCount);            // a, b, @id, text
        Assert.Equal(4, jsonDoc.NodeCount);           // object, array, 1, 2
        Assert.Equal(3, xmlDoc.RawSource.LineCount);  // the raw text is always there
        Assert.Equal("  <b id=\"1\">x</b>", xmlDoc.RawSource.ReadLines(1, 1)[0]);
    }

    [Fact]
    public async Task Invalid_content_falls_back_to_the_raw_text_and_says_why()
    {
        var broken = Write("bad.xml", "plain words, no markup at all", ArtifactType.Xml);

        var result = await Loader().LoadAsync(broken, CancellationToken.None);

        Assert.IsType<TextDocument>(result.Document);
        Assert.Contains("StructuredFileParser failed", result.FailureMessage);
        Assert.Contains("no XML elements", result.FailureMessage);
    }

    [Fact]
    public async Task Xml_cut_off_after_some_elements_opens_as_a_partial_tree_with_the_reason()
    {
        var cut = Write("diagerr.xml", "<?xml version=\"1.0\"?>\n<xml>\n<schema><row n=\"1\"/></schema>\n<data>\n", ArtifactType.Xml);

        var result = await Loader().LoadAsync(cut, CancellationToken.None);

        var doc = Assert.IsType<StructuredDocument>(result.Document);
        Assert.Null(result.FailureMessage);
        Assert.Contains("Unexpected end of file", doc.ReadProblem);
        Assert.True(doc.ReadProblemLine >= 4);
        Assert.NotNull(doc.FindNode("/xml/schema"));
    }

    [Fact]
    public async Task Json_cut_off_after_some_values_opens_as_a_partial_tree_with_the_reason()
    {
        var cut = Write("state.json", "{\n  \"device\": \"A1\",\n  \"checks\": [\n    { \"id\": 1 },\n    { \"id\": 2", ArtifactType.Json);

        var result = await Loader().LoadAsync(cut, CancellationToken.None);

        var doc = Assert.IsType<StructuredDocument>(result.Document);
        Assert.Null(result.FailureMessage);
        Assert.NotNull(doc.ReadProblem);
        Assert.True(doc.ReadProblemLine >= 4);
        Assert.NotNull(doc.FindNode("/device"));
    }

    [Fact]
    public async Task A_json_log_that_is_not_one_document_still_opens()
    {
        var lines = Write("log.json", "{\"level\":\"info\"}\n{\"level\":\"error\"}\n", ArtifactType.Json);

        var doc = Assert.IsType<StructuredDocument>((await Loader().LoadAsync(lines, CancellationToken.None)).Document);

        Assert.Equal(StructuredNodeKind.Document, doc.Root.Kind);
        Assert.Equal(2, doc.Root.Children.Count);
    }

    [Fact]
    public async Task Files_over_the_size_limit_are_left_to_the_text_viewer_without_an_error()
    {
        var path = Path.Combine(_dir, "huge.xml");
        await using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength(StructuredFileParser.MaxFileBytes + 1);
        }

        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = "huge.xml",
            OriginalPath = "huge.xml",
            ExtractedPath = path,
            Provenance = new[] { "huge.xml" },
            ArtifactType = ArtifactType.Xml,
        };

        Assert.False(new StructuredFileParser().CanHandle(artifact));
        var result = await Loader().LoadAsync(artifact, CancellationToken.None);
        Assert.IsType<TextDocument>(result.Document);
        Assert.Null(result.FailureMessage);
    }

    [Theory]
    [InlineData(ArtifactType.TextLog, false)]
    [InlineData(ArtifactType.Html, false)]
    [InlineData(ArtifactType.RegistryExport, false)]
    [InlineData(ArtifactType.Xml, true)]
    [InlineData(ArtifactType.Json, true)]
    public void Only_xml_and_json_artifacts_with_a_working_copy_are_handled(ArtifactType type, bool expected)
    {
        var artifact = Write("x.dat", "<a/>", type);

        Assert.Equal(expected, new StructuredFileParser().CanHandle(artifact));
        Assert.False(new StructuredFileParser().CanHandle(artifact with { ExtractedPath = null }));
        Assert.False(new StructuredFileParser().CanHandle(artifact with { ExtractedPath = Path.Combine(_dir, "missing") }));
    }

    [Fact]
    public async Task A_file_changing_size_between_check_and_read_does_not_break_parsing()
    {
        var artifact = Write("a.json", "{\"a\":1}", ArtifactType.Json);
        Assert.True(new StructuredFileParser().CanHandle(artifact));
        File.WriteAllText(artifact.ExtractedPath!, "{\"a\":1,\"b\":2}");

        var doc = (StructuredDocument)await new StructuredFileParser().ParseAsync(artifact, CancellationToken.None);

        Assert.Equal(2, doc.Root.Children.Count);
    }

    // ---- within-document search ----

    private async Task<StructuredDocument> Doc(string name, string content, ArtifactType type) =>
        (StructuredDocument)await new StructuredFileParser().ParseAsync(Write(name, content, type), CancellationToken.None);

    [Fact]
    public async Task Search_finds_names_and_values_in_document_order()
    {
        var doc = await Doc("a.json", "{ \"retry\": 3, \"items\": [ { \"name\": \"Retry policy\" }, { \"retries\": 5 } ] }", ArtifactType.Json);

        var result = StructuredSearch.Find(doc, "retr", matchCase: false, CancellationToken.None);

        Assert.Equal(
            new[] { "/retry", "/items/0/name", "/items/1/retries" },
            result.Matches.Select(m => doc.PathOf(m.Node)));
        Assert.Equal(
            new[] { StructuredMatchField.Name, StructuredMatchField.Value, StructuredMatchField.Name },
            result.Matches.Select(m => m.Field));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Search_can_match_case_and_covers_xml_attributes_text_and_comments()
    {
        var doc = await Doc("a.xml", "<Root Mode=\"Fast\"><!-- fast path --><Item>fast</Item></Root>", ArtifactType.Xml);

        var insensitive = StructuredSearch.Find(doc, "fast", matchCase: false, CancellationToken.None);
        var sensitive = StructuredSearch.Find(doc, "fast", matchCase: true, CancellationToken.None);

        Assert.Equal(3, insensitive.Matches.Count);
        Assert.Equal(
            new[] { "/Root[1]/@Mode", "/Root[1]/comment()[1]", "/Root[1]/Item[1]/text()[1]" },
            insensitive.Matches.Select(m => doc.PathOf(m.Node)));
        Assert.Equal(2, sensitive.Matches.Count); // "Fast" does not match
    }

    [Fact]
    public async Task Search_caps_its_matches_and_says_so()
    {
        var doc = await Doc("a.json", "[" + string.Join(",", Enumerable.Repeat("\"hit\"", 50)) + "]", ArtifactType.Json);

        var result = StructuredSearch.Find(doc, "hit", false, CancellationToken.None, maxMatches: 10);

        Assert.Equal(10, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Empty(StructuredSearch.Find(doc, "", false, CancellationToken.None).Matches);
    }

    // ---- global search still covers structured files ----

    [Fact]
    public async Task Global_search_finds_text_in_xml_and_json_by_line()
    {
        var xml = Write("a.xml", "<a>\n  <b>needle here</b>\n</a>\n", ArtifactType.Xml);
        var json = Write("a.json", "{\n  \"k\": \"needle\"\n}\n", ArtifactType.Json);
        var service = new GlobalSearchService(Loader());

        var results = new List<ArtifactSearchResult>();
        await foreach (var update in service.SearchAsync(
                           new[] { xml, json }, SearchQueryParser.Parse("needle").Query, new SearchOptions(), CancellationToken.None))
        {
            if (update.Result is not null)
            {
                results.Add(update.Result);
            }
        }

        Assert.Equal(2, results.Count);
        Assert.Equal(2, results.Single(r => r.Artifact.Name == "a.xml").Hits.Single().Location.NumericPosition);
        Assert.Equal(2, results.Single(r => r.Artifact.Name == "a.json").Hits.Single().Location.NumericPosition);
    }
}
