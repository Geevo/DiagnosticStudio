using System.Text;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers.Structured;

namespace DiagnosticStudio.Tests.Parsers;

public class StructuredReaderTests
{
    private static (StructuredNode Root, int Count) Xml(string xml, int maxDepth = XmlStructureReader.DefaultMaxDepth) =>
        XmlStructureReader.Read(new MemoryStream(new UTF8Encoding(false).GetBytes(xml)), CancellationToken.None, maxDepth);

    private static (StructuredNode Root, int Count) Json(string json, int maxDepth = JsonStructureReader.DefaultMaxDepth) =>
        JsonStructureReader.Read(Encoding.UTF8.GetBytes(json), CancellationToken.None, maxDepth);

    private static IEnumerable<StructuredNode> All(StructuredNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var node in All(child))
            {
                yield return node;
            }
        }
    }

    // ---- XML ----

    private const string Config =
        "<?xml version=\"1.0\"?>\n" +
        "<Config version=\"2\" xmlns:p=\"urn:x\">\n" +        // line 2
        "  <!-- settings -->\n" +                            // 3
        "  <Item id=\"a\">first</Item>\n" +                  // 4
        "  <Item id=\"b\">\n" +                              // 5
        "    <p:Name>second</p:Name>\n" +                    // 6
        "    <![CDATA[raw <text>]]>\n" +                     // 7
        "  </Item>\n" +                                      // 8
        "  <Empty />\n" +                                    // 9
        "</Config>\n";

    [Fact]
    public void Xml_elements_attributes_text_comments_and_cdata_are_all_nodes()
    {
        var (root, count) = Xml(Config);

        Assert.Equal(StructuredNodeKind.Document, root.Kind);
        var config = Assert.Single(root.Children);
        Assert.Equal("Config", config.Name);
        Assert.Equal(new[] { "version", "xmlns:p" }, config.Children.Where(c => c.Kind == StructuredNodeKind.Attribute).Select(c => c.Name));
        Assert.Equal("2", config.Children.First(c => c.Name == "version").Value);
        Assert.Contains(config.Children, c => c.Kind == StructuredNodeKind.Comment && c.Value == " settings ");

        var items = config.Children.Where(c => c.Name == "Item").ToList();
        Assert.Equal(new[] { 1, 2 }, items.Select(i => i.Ordinal));
        Assert.Equal("first", Assert.Single(items[0].Children, c => c.Kind == StructuredNodeKind.Text).Value);
        Assert.Equal("raw <text>", items[1].Children.Last(c => c.Kind == StructuredNodeKind.Text).Value);
        Assert.Equal("second", items[1].Children.Single(c => c.Name == "p:Name").Children.Single().Value);
        Assert.Empty(config.Children.Single(c => c.Name == "Empty").Children);
        Assert.Equal(All(root).Count() - 1, count); // every node but the synthetic document
    }

    [Fact]
    public void Xml_nodes_carry_the_line_they_start_on()
    {
        var (root, _) = Xml(Config);
        var config = root.Children[0];

        Assert.Equal(2, config.Line);
        Assert.Equal(4, config.Children.First(c => c.Name == "Item").Line);
        Assert.Equal(5, config.Children.Last(c => c.Name == "Item").Line);
        Assert.Equal(6, config.Children.Last(c => c.Name == "Item").Children.First(c => c.Name == "p:Name").Line);
        Assert.Equal(9, config.Children.Single(c => c.Name == "Empty").Line);
    }

    [Fact]
    public void Xml_paths_round_trip_for_every_node()
    {
        var (root, _) = Xml(Config);
        var document = new StructuredDocument
        {
            Artifact = null!,
            Format = StructuredFormat.Xml,
            Root = root,
            RawSource = null!,
        };

        foreach (var node in All(root))
        {
            var path = document.PathOf(node);
            Assert.Same(node, document.FindNode(path));
        }

        Assert.Equal("/", document.PathOf(root));
        Assert.Equal("/Config[1]/Item[2]/p:Name[1]", document.PathOf(root.Children[0].Children.Last(c => c.Name == "Item").Children.First(c => c.Name == "p:Name")));
        Assert.Equal("/Config[1]/@version", document.PathOf(root.Children[0].Children.First(c => c.Name == "version")));
        Assert.Null(document.FindNode("/Config[1]/Item[3]"));
        Assert.Null(document.FindNode("/Nope[1]"));
        Assert.Null(document.FindNode("relative/path"));
    }

    [Fact]
    public void Xml_with_several_top_level_elements_is_accepted_as_a_fragment()
    {
        var (root, _) = Xml("<Event id=\"1\"/>\n<Event id=\"2\"/>\n<Other/>");

        Assert.Equal(new[] { "Event", "Event", "Other" }, root.Children.Select(c => c.Name));
        Assert.Equal(new[] { 1, 2, 1 }, root.Children.Select(c => c.Ordinal));
    }

    [Fact]
    public void Insignificant_whitespace_between_elements_is_not_a_node()
    {
        var (root, _) = Xml("<a>\n  <b/>\n  <b/>\n</a>");

        Assert.All(root.Children[0].Children, c => Assert.Equal(StructuredNodeKind.Element, c.Kind));
    }

    [Theory]
    [InlineData("<a><b></a>")]
    [InlineData("<a>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    [InlineData("<a>&undefined;</a>")]
    public void Malformed_or_empty_xml_is_rejected_with_a_clear_error(string xml)
    {
        Assert.Throws<InvalidDataException>(() => Xml(xml));
    }

    [Fact]
    public void Xml_nesting_beyond_the_limit_is_rejected_without_overflowing_the_stack()
    {
        var deep = string.Concat(Enumerable.Repeat("<a>", 100_000)) + string.Concat(Enumerable.Repeat("</a>", 100_000));

        var ex = Assert.Throws<InvalidDataException>(() => Xml(deep));

        Assert.Contains("nested deeper", ex.Message);
    }

    [Fact]
    public void Xml_nesting_within_the_limit_is_read_iteratively()
    {
        var deep = string.Concat(Enumerable.Repeat("<a>", 400)) + "x" + string.Concat(Enumerable.Repeat("</a>", 400));

        var (root, count) = Xml(deep);

        Assert.Equal(401, count);
        var node = root;
        var depth = 0;
        while (node.Children.Count > 0)
        {
            node = node.Children[0];
            depth++;
        }

        Assert.Equal(401, depth);
    }

    [Fact]
    public void An_external_entity_is_never_fetched()
    {
        // If the DTD were processed, this would read a local file into the document.
        var secret = Path.Combine(Path.GetTempPath(), "ds-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xml = $"<!DOCTYPE d [<!ENTITY x SYSTEM \"file:///{secret.Replace('\\', '/')}\">]><d>&x;</d>";

            // Rejected (the entity is undeclared once the DTD is ignored) or read without the content; never expanded.
            try
            {
                var (root, _) = Xml(xml);
                Assert.DoesNotContain(All(root), n => n.Value?.Contains("TOP-SECRET", StringComparison.Ordinal) == true);
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void An_entity_expansion_bomb_does_not_expand()
    {
        var xml =
            "<!DOCTYPE lolz [<!ENTITY lol \"lol\">" +
            "<!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">" +
            "<!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">" +
            "<!ENTITY lol4 \"&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;\">" +
            "]><lolz>&lol4;</lolz>";

        // Either the document is refused or whatever was read stays small; expanding it would be enormous.
        try
        {
            var (root, _) = Xml(xml);
            Assert.True(All(root).Sum(n => n.ValueLength) < 1000);
        }
        catch (InvalidDataException)
        {
        }
    }

    [Fact]
    public void A_node_cap_stops_runaway_documents()
    {
        var wide = "<r>" + string.Concat(Enumerable.Repeat("<i/>", 100)) + "</r>";

        var ex = Assert.Throws<InvalidDataException>(() =>
            XmlStructureReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(wide)), CancellationToken.None, maxNodes: 50));

        Assert.Contains("more than 50", ex.Message);
    }

    [Fact]
    public void Very_long_text_is_cut_and_flagged_but_its_length_is_known()
    {
        var (root, _) = Xml("<a>" + new string('x', 50_000) + "</a>");

        var text = root.Children[0].Children[0];
        Assert.True(text.IsValueTruncated);
        Assert.Equal(StructuredNode.MaxStoredValueChars, text.Value!.Length);
        Assert.Equal(50_000, text.ValueLength);
    }

    // ---- JSON ----

    private const string Sample =
        "{\n" +                                    // 1
        "  \"name\": \"agent\",\n" +               // 2
        "  \"retries\": 3,\n" +                    // 3
        "  \"ratio\": 1.50e+3,\n" +                // 4
        "  \"enabled\": true,\n" +                 // 5
        "  \"missing\": null,\n" +                 // 6
        "  \"items\": [\n" +                       // 7
        "    { \"id\": 1 },\n" +                   // 8
        "    { \"id\": 2, \"tags\": [\"a\", \"b\"] }\n" + // 9
        "  ],\n" +                                 // 10
        "  \"a/b\": { \"c~d\": \"x\" }\n" +        // 11
        "}\n";

    [Fact]
    public void Json_values_keep_their_kind_and_original_text()
    {
        var (root, count) = Json(Sample);

        Assert.Equal(StructuredNodeKind.Object, root.Kind);
        Assert.Null(root.Parent);
        Assert.Equal(new[] { "name", "retries", "ratio", "enabled", "missing", "items", "a/b" }, root.Children.Select(c => c.Name));
        Assert.Equal(StructuredNodeKind.String, root.Children[0].Kind);
        Assert.Equal("agent", root.Children[0].Value);
        Assert.Equal("1.50e+3", root.Children[2].Value);              // not rounded to 1500
        Assert.Equal(StructuredNodeKind.Boolean, root.Children[3].Kind);
        Assert.Equal(StructuredNodeKind.Null, root.Children[4].Kind);
        Assert.Equal(StructuredNodeKind.Array, root.Children[5].Kind);
        Assert.Equal(All(root).Count(), count);
    }

    [Fact]
    public void Json_nodes_carry_the_line_they_start_on()
    {
        var (root, _) = Json(Sample);

        Assert.Equal(1, root.Line);
        Assert.Equal(2, root.Children[0].Line);
        Assert.Equal(7, root.Children[5].Line);
        Assert.Equal(8, root.Children[5].Children[0].Line);
        Assert.Equal(9, root.Children[5].Children[1].Children[1].Line);
        Assert.Equal(11, root.Children[6].Children[0].Line);
    }

    [Fact]
    public void Json_pointers_round_trip_for_every_node_including_escaped_keys()
    {
        var (root, _) = Json(Sample);
        var document = new StructuredDocument { Artifact = null!, Format = StructuredFormat.Json, Root = root, RawSource = null! };

        foreach (var node in All(root))
        {
            Assert.Same(node, document.FindNode(document.PathOf(node)));
        }

        var tag = root.Children[5].Children[1].Children[1].Children[1];
        Assert.Equal("/items/1/tags/1", document.PathOf(tag));
        Assert.Equal("b", tag.Value);
        Assert.Equal("/a~1b/c~0d", document.PathOf(root.Children[6].Children[0]));
        Assert.Equal(string.Empty, document.PathOf(root));
        Assert.Same(root, document.FindNode(string.Empty));
        Assert.Null(document.FindNode("/items/9"));
        Assert.Null(document.FindNode("/nope"));
        Assert.Null(document.FindNode("items"));
    }

    [Fact]
    public void Json_lenient_input_is_accepted()
    {
        var (root, _) = Json("// leading comment\n{ /* inline */ \"a\": [1, 2,], \"b\": 1, }");

        Assert.Equal(new[] { "a", "b" }, root.Children.Select(c => c.Name));
        Assert.Equal(2, root.Children[0].Children.Count);
    }

    [Fact]
    public void Json_lines_become_a_document_of_top_level_values()
    {
        var (root, _) = Json("{\"n\":1}\n{\"n\":2}\n[3]\n");

        Assert.Equal(StructuredNodeKind.Document, root.Kind);
        Assert.Equal(3, root.Children.Count);
        var document = new StructuredDocument { Artifact = null!, Format = StructuredFormat.Json, Root = root, RawSource = null! };
        Assert.Equal("/1/n", document.PathOf(root.Children[1].Children[0]));
        Assert.Same(root.Children[1].Children[0], document.FindNode("/1/n"));
        Assert.Equal(new[] { 1, 2, 3 }, root.Children.Select(c => c.Line));
    }

    [Fact]
    public void Json_duplicate_keys_are_all_kept()
    {
        var (root, _) = Json("{\"a\":1,\"a\":2}");

        Assert.Equal(new[] { "1", "2" }, root.Children.Select(c => c.Value));
    }

    [Fact]
    public void Json_strings_are_unescaped()
    {
        var (root, _) = Json("{\"s\":\"line1\\nline2 \\u00e9 \\\"q\\\"\"}");

        Assert.Equal("line1\nline2 é \"q\"", root.Children[0].Value);
    }

    [Fact]
    public void Json_with_a_byte_order_mark_or_in_utf16_is_read()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}")).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("{\n\"a\":\n1}")).ToArray();

        Assert.Equal(2, JsonStructureReader.Read(withBom).Root.Children.Count);
        var (root, _) = JsonStructureReader.Read(utf16);
        Assert.Equal("1", root.Children[0].Value);
        Assert.Equal(3, root.Children[0].Line);
    }

    [Theory]
    [InlineData("{\"a\":")]
    [InlineData("{a:1}")]
    [InlineData("[1,2")]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("nope")]
    public void Invalid_or_empty_json_is_rejected_with_a_clear_error(string json)
    {
        Assert.Throws<InvalidDataException>(() => Json(json));
    }

    [Fact]
    public void Json_nesting_beyond_the_limit_is_rejected_without_overflowing_the_stack()
    {
        var deep = new string('[', 100_000) + new string(']', 100_000);

        Assert.Throws<InvalidDataException>(() => Json(deep));
    }

    [Fact]
    public void A_json_scalar_at_the_top_level_is_a_document_of_one_value()
    {
        var (root, _) = Json("42");

        Assert.Equal(StructuredNodeKind.Number, root.Kind);
        Assert.Equal("42", root.Value);
    }

    // ---- NodeAtLine ----

    [Fact]
    public void A_source_line_maps_to_the_last_node_starting_on_or_before_it()
    {
        var (root, _) = Json(Sample);
        var document = new StructuredDocument { Artifact = null!, Format = StructuredFormat.Json, Root = root, RawSource = null! };

        Assert.Equal(string.Empty, document.PathOf(document.NodeAtLine(1)!));
        Assert.Equal("/name", document.PathOf(document.NodeAtLine(2)!));
        Assert.Equal("/items/0/id", document.PathOf(document.NodeAtLine(8)!));
        Assert.Equal("/items/1/tags/1", document.PathOf(document.NodeAtLine(9)!));
        Assert.Equal("/a~1b/c~0d", document.PathOf(document.NodeAtLine(11)!));
        Assert.Equal("/a~1b/c~0d", document.PathOf(document.NodeAtLine(500)!)); // beyond the end: the last node
    }

    [Fact]
    public void A_source_line_before_any_xml_element_maps_to_nothing()
    {
        var (root, _) = Xml("\n\n\n<a/>");
        var document = new StructuredDocument { Artifact = null!, Format = StructuredFormat.Xml, Root = root, RawSource = null! };

        Assert.Null(document.NodeAtLine(1));
        Assert.Equal("/a[1]", document.PathOf(document.NodeAtLine(4)!));
    }
}

public class StructuredReaderTolerantTests
{
    private static (StructuredNode Root, int Count, XmlStructureReader.ReadStop? Stop) Tolerant(string xml) =>
        XmlStructureReader.ReadTolerant(new MemoryStream(new UTF8Encoding(false).GetBytes(xml)), CancellationToken.None);

    [Fact]
    public void A_file_cut_off_part_way_gives_the_tree_so_far_and_says_where_it_stopped()
    {
        var (root, count, stop) = Tolerant("<xml>\n<schema>\n<a b=\"1\"/>\n</schema>\n<data>\n");

        Assert.NotNull(stop);
        Assert.Contains("Unexpected end of file", stop!.Message);
        Assert.True(stop.Line >= 5);
        Assert.True(count >= 4);
        Assert.Contains(root.Children, c => c.Name == "xml");
    }

    [Fact]
    public void A_well_formed_file_has_no_stop()
    {
        var (_, _, stop) = Tolerant("<a><b/></a>");

        Assert.Null(stop);
    }

    [Fact]
    public void Text_with_no_element_before_the_fault_still_fails()
    {
        Assert.ThrowsAny<Exception>(() => Tolerant("<"));
        Assert.Throws<InvalidDataException>(() => Tolerant("just some words"));
    }

    [Fact]
    public void The_strict_reader_still_rejects_a_cut_off_file()
    {
        Assert.Throws<InvalidDataException>(() =>
            XmlStructureReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("<a><b>")), CancellationToken.None));
    }
}

public class JsonTolerantReaderTests
{
    private static (StructuredNode Root, int Count, XmlStructureReader.ReadStop? Stop) Tolerant(string json) =>
        JsonStructureReader.ReadTolerant(new UTF8Encoding(false).GetBytes(json), CancellationToken.None);

    [Fact]
    public void A_document_cut_off_part_way_gives_the_tree_so_far_and_says_where_it_stopped()
    {
        var (root, count, stop) = Tolerant("{\n  \"name\": \"a\",\n  \"items\": [1, 2,\n    3, \"x");

        Assert.NotNull(stop);
        Assert.True(stop!.Line >= 3);
        Assert.Equal(StructuredNodeKind.Object, root.Kind);
        Assert.Contains(root.Children, c => c.Name == "name");
        var items = Assert.Single(root.Children, c => c.Name == "items");
        Assert.Equal(3, items.Children.Count);
        Assert.True(count >= 5);
    }

    [Fact]
    public void Garbage_after_a_complete_value_keeps_the_value()
    {
        var (root, _, stop) = Tolerant("{\"a\":1}\n<<< not json");

        Assert.NotNull(stop);
        Assert.Equal(2, stop!.Line);
        Assert.Contains(root.Children, c => c.Name == "a");
    }

    [Fact]
    public void A_valid_document_has_no_stop()
    {
        var (_, _, stop) = Tolerant("[1, 2, 3]");

        Assert.Null(stop);
    }

    [Fact]
    public void Content_with_no_value_before_the_fault_still_fails()
    {
        Assert.Throws<InvalidDataException>(() => Tolerant("not json at all"));
        Assert.Throws<InvalidDataException>(() => Tolerant("   "));
    }

    [Fact]
    public void The_strict_reader_still_rejects_a_cut_off_document()
    {
        Assert.Throws<InvalidDataException>(() =>
            JsonStructureReader.Read(Encoding.UTF8.GetBytes("{\"a\": [1,"), CancellationToken.None));
    }
}
