using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.Parsers;

/// <summary>
/// Exports written by tools other than regedit (no header, text written exactly as stored): strings that hold
/// unescaped quotes, single backslashes, and XML spread over several lines. From the Intune management extension's
/// registry export.
/// </summary>
public sealed class RegFileParserToolExportTests
{
    private static readonly DiagnosticArtifact Artifact = new()
    {
        Id = Guid.NewGuid(),
        Name = "tool.reg",
        OriginalPath = "tool.reg",
        Provenance = new[] { "Bundle", "tool.reg" },
        ArtifactType = ArtifactType.RegistryExport,
    };

    private sealed class NoSource : ITextLineSource
    {
        public int LineCount => 0;
        public long ByteLength => 0;
        public string EncodingName => "test";
        public IReadOnlyList<string> ReadLines(int startLine, int count) => Array.Empty<string>();
        public IEnumerable<string> EnumerateLines(int startLine = 0) => Array.Empty<string>();
    }

    private static RegistryDocument Parse(string text) =>
        RegFileParser.Parse(Artifact, text.Replace("\r\n", "\n").Split('\n'), new NoSource());

    private const string Key = @"[HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework]";

    private static RegistryKey KeyOf(RegistryDocument doc, string path) => doc.FindKey(path)!;

    // ---- strings that contain quotes ----

    [Fact]
    public void A_string_with_unescaped_quotes_is_read_to_its_last_quote()
    {
        var doc = Parse(Key + "\n    \"ResultDetails\"=\"{\"Version\":1,\"SigningCode\":649,\"ExecutionMsg\":\"\"}\"\n");

        var value = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue("ResultDetails")!;
        Assert.Equal("{\"Version\":1,\"SigningCode\":649,\"ExecutionMsg\":\"\"}", value.DisplayValue);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void Backslashes_in_a_file_without_a_header_are_kept_as_written()
    {
        var doc = Parse(Key + "\n\"Bookmark\"=\"<![CDATA[{\"Text\":\"<List>\\r\\n  <B Channel='X' IsCurrent='true'\\/>\"}]]>\"\n\"Path\"=\"C:\\\\Program Files\\\\Agent\"\n");

        var key = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework");
        Assert.Equal("<![CDATA[{\"Text\":\"<List>\\r\\n  <B Channel='X' IsCurrent='true'\\/>\"}]]>", key.FindValue("Bookmark")!.DisplayValue);
        Assert.Equal("C:\\\\Program Files\\\\Agent", key.FindValue("Path")!.DisplayValue);
    }

    [Fact]
    public void A_regedit_export_with_a_header_still_unescapes()
    {
        var doc = Parse("Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\Software\\X]\n\"A\"=\"say \\\"hi\\\" at C:\\\\Temp\"\n");

        Assert.Equal("say \"hi\" at C:\\Temp", KeyOf(doc, @"HKEY_CURRENT_USER\Software\X").FindValue("A")!.DisplayValue);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void A_value_with_a_header_that_holds_an_unescaped_quote_is_taken_as_written()
    {
        // Cannot be a regedit export of that value; reading it as one would cut it at the first quote.
        var doc = Parse("Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\Software\\X]\n\"A\"=\"<?xml version=\"1.0\"?>\"\n");

        Assert.Equal("<?xml version=\"1.0\"?>", KeyOf(doc, @"HKEY_CURRENT_USER\Software\X").FindValue("A")!.DisplayValue);
    }

    [Fact]
    public void A_name_that_contains_a_backslash_is_kept_in_a_file_without_a_header()
    {
        var doc = Parse("[HKEY_CURRENT_USER\\Software\\X]\n\"Dir\\Name\"=\"v\"\n");

        Assert.NotNull(KeyOf(doc, @"HKEY_CURRENT_USER\Software\X").FindValue("Dir\\Name"));
    }

    // ---- values spread over several lines ----

    private const string BookmarkBlock =
        "    \"bookmark\"=\"<?xml version=\"1.0\" encoding=\"utf-16\"?>\n"
        + "<ArrayOfBookmarkGuidPair xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n"
        + "  <BookmarkGuidPair QueryGuid=\"488bf6dd\"\n"
        + "    Bookmark=\"&lt;![CDATA[x]]&gt;\" />\n"
        + "</ArrayOfBookmarkGuidPair>\"\n";

    [Fact]
    public void A_string_value_that_runs_over_several_lines_is_one_value()
    {
        var doc = Parse(Key + "\n" + BookmarkBlock + "\n[HKEY_LOCAL_MACHINE\\software\\microsoft\\intunemanagementextension\\Settings]\n\"CheckInTotalCount\"=dword:00000001\n");

        var value = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue("bookmark")!;
        Assert.Empty(doc.Issues);
        Assert.Equal(RegistryValueKind.String, value.Kind);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<ArrayOfBookmarkGuidPair", value.DetailText);
        Assert.EndsWith("</ArrayOfBookmarkGuidPair>", value.DetailText);
        Assert.Contains("  <BookmarkGuidPair QueryGuid=\"488bf6dd\"\r\n    Bookmark=", value.DetailText);
        Assert.Equal(2, doc.ValueCount);
    }

    [Fact]
    public void A_multi_line_value_is_shown_on_one_row_with_the_text_in_the_detail()
    {
        var doc = Parse(Key + "\n" + BookmarkBlock);

        var value = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue("bookmark")!;
        Assert.DoesNotContain('\n', value.DisplayValue);
        Assert.DoesNotContain('\r', value.DisplayValue);
        Assert.Contains("\u21B5", value.DisplayValue);
        Assert.Contains("\n", value.DetailText);
    }

    [Fact]
    public void Lines_after_a_multi_line_value_keep_their_own_line_numbers()
    {
        // line 1 key, 2-6 bookmark (5 lines), 7 blank, 8 key, 9 value
        var doc = Parse(Key + "\n" + BookmarkBlock + "\n[HKEY_LOCAL_MACHINE\\software\\X]\n\"After\"=\"v\"\n");

        Assert.Equal(2, KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue("bookmark")!.SourceLine);
        Assert.Equal(9, KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\X").FindValue("After")!.SourceLine);
        Assert.Equal(8, KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\X").SourceLine);
    }

    [Fact]
    public void A_line_inside_the_value_that_ends_with_a_quote_does_not_end_it()
    {
        var doc = Parse(Key + "\n\"v\"=\"<a\nb=\"1\"\n  c=\"2\"\n/>\"\n\"next\"=\"x\"\n");

        var key = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework");
        Assert.Equal("<a\r\nb=\"1\"\r\n  c=\"2\"\r\n/>", key.FindValue("v")!.DetailText);
        Assert.Equal("x", key.FindValue("next")!.DisplayValue);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void Several_multi_line_values_in_a_row_each_stay_whole()
    {
        var doc = Parse(Key + "\n\"a\"=\"<x>\n</x>\"\n\"b\"=\"<y>\n</y>\"\n\"c\"=dword:00000002\n");

        var key = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework");
        Assert.Equal("<x>\r\n</x>", key.FindValue("a")!.DetailText);
        Assert.Equal("<y>\r\n</y>", key.FindValue("b")!.DetailText);
        Assert.Equal(RegistryValueKind.DWord, key.FindValue("c")!.Kind);
        Assert.Equal(3, doc.ValueCount);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void A_multi_line_default_value_works_too()
    {
        var doc = Parse(Key + "\n@=\"<x>\n</x>\"\n");

        Assert.Equal("<x>\r\n</x>", KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue(string.Empty)!.DetailText);
    }

    [Fact]
    public void A_string_that_never_closes_ends_at_the_next_key_and_keeps_what_it_has()
    {
        var doc = Parse(Key + "\n\"open\"=\"first line\nsecond line\n[HKEY_LOCAL_MACHINE\\software\\X]\n\"ok\"=\"1\"\n");

        var key = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework");
        Assert.Equal("first line\r\nsecond line", key.FindValue("open")!.DetailText);
        Assert.Equal("1", KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\X").FindValue("ok")!.DisplayValue);
    }

    [Fact]
    public void A_string_that_never_closes_at_the_end_of_the_file_is_kept()
    {
        var doc = Parse(Key + "\n\"open\"=\"first line\nsecond line");

        Assert.Equal("first line\r\nsecond line", KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue("open")!.DetailText);
    }

    [Fact]
    public void A_blank_line_inside_a_value_that_is_still_open_does_not_end_it()
    {
        var doc = Parse(Key + "\n\"v\"=\"para one\n\npara two\"\n");

        Assert.Equal("para one\r\n\r\npara two", KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework").FindValue("v")!.DetailText);
    }

    [Fact]
    public void Stray_text_outside_a_value_is_still_reported()
    {
        var doc = Parse(Key + "\n\"a\"=\"x\"\n<stray/>\n\"b\"=\"y\"\n");

        Assert.Equal(1, doc.TotalIssueCount);
        Assert.Equal(3, doc.Issues[0].Line);
        Assert.Equal(2, doc.ValueCount);
    }

    [Fact]
    public void Hex_values_that_continue_with_a_backslash_are_unaffected()
    {
        var doc = Parse(Key + "\n\"h\"=hex:01,02,\\\n  03,04\n\"after\"=\"v\"\n");

        var key = KeyOf(doc, @"HKEY_LOCAL_MACHINE\software\microsoft\intunemanagementextension\sensor\listenerFramework");
        Assert.Equal(4, key.FindValue("h")!.DataLength);
        Assert.Equal("v", key.FindValue("after")!.DisplayValue);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void A_runaway_value_is_cut_off_rather_than_swallowing_the_file()
    {
        var lines = new List<string> { Key, "\"open\"=\"start" };
        lines.AddRange(Enumerable.Repeat("filler line without a quote", 150_000));
        lines.Add("[HKEY_LOCAL_MACHINE\\software\\X]");
        lines.Add("\"later\"=\"v\"");

        var doc = RegFileParser.Parse(Artifact, lines, new NoSource());

        // The cut-off value ends the continuation; lines beyond it are reported rather than hidden.
        Assert.True(doc.TotalIssueCount > 0);
        Assert.Equal("v", doc.FindKey(@"HKEY_LOCAL_MACHINE\software\X")!.FindValue("later")!.DisplayValue);
    }
}
