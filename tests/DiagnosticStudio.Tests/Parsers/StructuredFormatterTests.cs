using System.Text;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers.Structured;

namespace DiagnosticStudio.Tests.Parsers;

public class StructuredFormatterTests
{
    private static string Json(string text) => StructuredFormatter.FormatJson(Encoding.UTF8.GetBytes(text));

    private static string Xml(string text) => StructuredFormatter.FormatXml(Encoding.UTF8.GetBytes(text));

    // ---- JSON ----

    [Fact]
    public void A_one_line_object_is_indented()
    {
        Assert.Equal(
            "{\n  \"a\": 1,\n  \"b\": [\n    true,\n    null,\n    \"x\"\n  ],\n  \"c\": {\n    \"d\": 2.50\n  }\n}",
            Json("{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":2.50}}"));
    }

    [Fact]
    public void Empty_objects_and_arrays_stay_on_one_line()
    {
        Assert.Equal("{\n  \"a\": {},\n  \"b\": []\n}", Json("{\"a\":{},\"b\":[ ]}"));
        Assert.Equal("[]", Json("[]"));
        Assert.Equal("{}", Json("{ }"));
    }

    [Fact]
    public void Strings_property_names_and_numbers_are_copied_exactly_as_written()
    {
        // Escapes, a non-ASCII letter, an exponent and trailing zeros all survive unchanged.
        var source = "{\"p\\u0041th\\/x\":\"line\\nbreak \\\"q\\\" \\u00e9 \u00e9\",\"n\":1.50e+3,\"m\":-0.0}";

        var formatted = Json(source);

        Assert.Contains("\"p\\u0041th\\/x\": \"line\\nbreak \\\"q\\\" \\u00e9 \u00e9\"", formatted);
        Assert.Contains("\"n\": 1.50e+3", formatted);
        Assert.Contains("\"m\": -0.0", formatted);
    }

    [Fact]
    public void The_formatted_text_means_the_same_as_the_original()
    {
        var source = "{\"a\":[1,2,{\"b\":\"c\"}],\"d\":{\"e\":null,\"f\":[[],{}]},\"g\":\"h\"}";

        using var original = System.Text.Json.JsonDocument.Parse(source);
        using var again = System.Text.Json.JsonDocument.Parse(Json(source));

        Assert.Equal(original.RootElement.GetRawText().Replace(" ", string.Empty), again.RootElement.GetRawText().Replace(" ", string.Empty).Replace("\n", string.Empty));
    }

    [Fact]
    public void Formatting_is_stable_when_applied_twice()
    {
        var once = Json("{\"a\":[1,{\"b\":2}],\"c\":\"d\"}");

        Assert.Equal(once, Json(once));
    }

    [Fact]
    public void Several_top_level_values_are_each_formatted_with_a_blank_line_between()
    {
        var formatted = Json("{\"a\":1}\n{\"a\":2}\n[3]");

        Assert.Equal("{\n  \"a\": 1\n}\n\n{\n  \"a\": 2\n}\n\n[\n  3\n]", formatted);
    }

    [Fact]
    public void A_top_level_scalar_is_left_alone()
    {
        Assert.Equal("\"just text\"", Json("  \"just text\" "));
        Assert.Equal("42", Json("42"));
    }

    [Fact]
    public void Trailing_commas_are_accepted_and_dropped()
    {
        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ]\n}", Json("{\"a\":[1,2,],}"));
    }

    [Fact]
    public void Comments_are_kept_on_their_own_lines()
    {
        var formatted = Json("{\n// note\n\"a\":1, /* block\ntext */ \"b\":2 // tail\n}");

        Assert.Contains("  // note\n  \"a\": 1,", formatted);
        Assert.Contains("/* block\ntext */\n  \"b\": 2", formatted);
        Assert.Contains("  // tail\n}", formatted);
    }

    [Fact]
    public void A_byte_order_mark_and_utf16_are_handled()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("{\"a\":1}")).ToArray();

        Assert.Equal("{\n  \"a\": 1\n}", StructuredFormatter.FormatJson(bom));
        Assert.Equal("{\n  \"a\": 1\n}", StructuredFormatter.FormatJson(utf16));
    }

    [Fact]
    public void Invalid_json_is_reported_not_guessed_at()
    {
        Assert.Throws<InvalidDataException>(() => Json("{\"a\":"));
        Assert.Throws<InvalidDataException>(() => Json("not json"));
    }

    [Fact]
    public void Very_deep_nesting_is_refused_rather_than_overflowing()
    {
        var deep = new string('[', 5000) + new string(']', 5000);

        Assert.Throws<InvalidDataException>(() => Json(deep));
    }

    [Fact]
    public void A_large_minified_file_keeps_its_structure_when_formatted()
    {
        var sb = new StringBuilder("{\"items\":[");
        for (var i = 0; i < 50_000; i++)
        {
            sb.Append(i == 0 ? string.Empty : ",").Append("{\"id\":").Append(i).Append(",\"name\":\"n").Append(i).Append("\",\"tags\":[\"a\",\"b\"]}");
        }

        sb.Append("]}");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        var formatted = StructuredFormatter.FormatJson(bytes);
        var (_, nodesBefore) = JsonStructureReader.Read(bytes);
        var (_, nodesAfter) = JsonStructureReader.Read(Encoding.UTF8.GetBytes(formatted));

        Assert.Equal(nodesBefore, nodesAfter);
        Assert.True(formatted.Count(c => c == '\n') > 400_000);
    }

    [Fact]
    public void Formatting_can_be_cancelled()
    {
        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("1", 300_000)) + "]");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => StructuredFormatter.FormatJson(bytes, cts.Token));
    }

    // ---- XML ----

    [Fact]
    public void A_one_line_document_is_indented()
    {
        Assert.Equal(
            "<Config>\n  <Item id=\"1\">one</Item>\n  <Group>\n    <Item id=\"2\" />\n  </Group>\n</Config>",
            Xml("<Config><Item id=\"1\">one</Item><Group><Item id=\"2\"/></Group></Config>"));
    }

    [Fact]
    public void The_declaration_is_kept_as_written()
    {
        var formatted = Xml("<?xml version=\"1.0\" encoding=\"utf-8\"?><a><b/></a>");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<a>", formatted);
    }

    [Fact]
    public void Comments_cdata_and_attributes_survive()
    {
        var formatted = Xml("<a x=\"1\" y='two'><!-- hi --><![CDATA[<raw & text>]]><b>t</b></a>");

        Assert.Contains("x=\"1\"", formatted);
        Assert.Contains("y=\"two\"", formatted);
        Assert.Contains("<!-- hi -->", formatted);
        Assert.Contains("<![CDATA[<raw & text>]]>", formatted);
    }

    [Fact]
    public void Existing_indentation_is_replaced_not_doubled()
    {
        var once = Xml("<a>\n      <b>\n\n    <c/>\n  </b>\n</a>");

        Assert.Equal("<a>\n  <b>\n    <c />\n  </b>\n</a>", once);
        Assert.Equal(once, Xml(once));
    }

    [Fact]
    public void Several_top_level_elements_are_accepted()
    {
        Assert.Equal("<a />\n<b>x</b>", Xml("<a/><b>x</b>"));
    }

    [Fact]
    public void A_document_with_a_dtd_is_refused_and_nothing_is_fetched()
    {
        // The structure reader refuses these too (plan 010), so no pretty view is offered for them. An external
        // entity must never be followed on the way.
        var ex = Assert.Throws<InvalidDataException>(() =>
            Xml("<?xml version=" + (char)34 + "1.0" + (char)34 + "?><!DOCTYPE a [<!ENTITY x SYSTEM " + (char)34 + "file:///C:/Windows/win.ini" + (char)34 + ">]><a>&x;</a>"));

        Assert.DoesNotContain("[fonts]", ex.Message);
    }

    [Fact]
    public void Malformed_xml_is_reported()
    {
        Assert.Throws<InvalidDataException>(() => Xml("<a><b></a>"));
    }

    // ---- the text source behind the formatted view ----

    [Fact]
    public void The_in_memory_source_presents_text_as_lines()
    {
        var source = new InMemoryTextSource("a\nb\nc\n", "test");

        Assert.Equal(3, source.LineCount);
        Assert.Equal(new[] { "b", "c" }, source.ReadLines(1, 10));
        Assert.Equal(new[] { "a", "b", "c" }, source.EnumerateLines());
        Assert.Equal(new[] { "c" }, source.EnumerateLines(2));
        Assert.Empty(source.ReadLines(3, 1));
        Assert.Equal(6, source.ByteLength);
        Assert.Equal("test", source.EncodingName);
    }

    [Fact]
    public void The_in_memory_source_reads_text_without_a_final_newline_and_a_leading_blank_line()
    {
        Assert.Equal(2, new InMemoryTextSource("a\nb", "t").LineCount);
        Assert.Equal(new[] { string.Empty, "x" }, new InMemoryTextSource("\nx", "t").EnumerateLines());
    }
}
