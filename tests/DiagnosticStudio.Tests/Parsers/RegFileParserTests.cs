using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.Parsers;

public sealed class RegFileParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-reg-" + Guid.NewGuid().ToString("N"));

    public RegFileParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DiagnosticArtifact Artifact = new()
    {
        Id = Guid.NewGuid(),
        Name = "test.reg",
        OriginalPath = "test.reg",
        Provenance = new[] { "Bundle", "test.reg" },
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

    private static RegistryDocument Parse(params string[] lines) =>
        RegFileParser.Parse(Artifact, lines, new NoSource());

    private static RegistryDocument Parse(string text) =>
        Parse(text.Replace("\r\n", "\n").Split('\n'));

    private const string Header = "Windows Registry Editor Version 5.00";

    // ---- structure ----

    [Fact]
    public void Builds_a_key_tree_with_hives_at_the_top()
    {
        var doc = Parse(Header, "", @"[HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Product]", "\"Name\"=\"Value\"");

        Assert.Equal(Header, doc.FormatHeader);
        var hive = Assert.Single(doc.Root.Children);
        Assert.Equal("HKEY_LOCAL_MACHINE", hive.Name);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Product", doc.FindKey(@"hkey_local_machine\software\vendor\product")!.FullPath);
        Assert.Equal(4, doc.KeyCount); // hive + 3 levels
        Assert.Equal(1, doc.ValueCount);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void Keys_list_is_in_file_order_and_includes_implied_ancestors()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\A\B]", @"[HKEY_CURRENT_USER\C]");

        Assert.Equal(
            new[] { "HKEY_CURRENT_USER", @"HKEY_CURRENT_USER\A", @"HKEY_CURRENT_USER\A\B", @"HKEY_CURRENT_USER\C" },
            doc.Keys.Select(k => k.FullPath));
        Assert.False(doc.FindKey(@"HKEY_CURRENT_USER\A")!.HasHeader);
        Assert.True(doc.FindKey(@"HKEY_CURRENT_USER\A\B")!.HasHeader);
    }

    [Fact]
    public void Repeated_key_sections_merge_and_later_values_win()
    {
        var doc = Parse(
            Header,
            @"[HKEY_CURRENT_USER\K]", "\"A\"=\"first\"", "\"B\"=\"keep\"",
            @"[HKEY_CURRENT_USER\K]", "\"a\"=\"second\"");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal(2, key.Values.Count);
        Assert.Equal("second", key.FindValue("A")!.DisplayValue);
        Assert.Equal(new[] { "a", "B" }, key.Values.Select(v => v.Name));
    }

    [Fact]
    public void Key_deletion_and_value_deletion_are_recorded()
    {
        var doc = Parse(Header, @"[-HKEY_CURRENT_USER\Gone]", @"[HKEY_CURRENT_USER\K]", "\"Old\"=-", "@=-");

        Assert.True(doc.FindKey(@"HKEY_CURRENT_USER\Gone")!.IsDeleted);
        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.All(key.Values, v => Assert.True(v.IsDeleted));
        Assert.Equal(RegistryValueKind.Deleted, key.FindValue("Old")!.Kind);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var doc = Parse(Header, "; a comment", "", @"[HKEY_CURRENT_USER\K]", "  ; indented comment", "\"A\"=\"1\"");

        Assert.Empty(doc.Issues);
        Assert.Equal(1, doc.ValueCount);
    }

    [Fact]
    public void Regedit4_header_is_recognised()
    {
        Assert.Equal("REGEDIT4", Parse("REGEDIT4", @"[HKEY_CURRENT_USER\K]").FormatHeader);
    }

    // ---- values ----

    [Fact]
    public void Default_value_and_escaped_names_and_strings_are_decoded()
    {
        var doc = Parse(
            Header, @"[HKEY_CURRENT_USER\K]",
            "@=\"default text\"",
            "\"Path\"=\"C:\\\\Program Files\\\\App\"",
            "\"Quote\"=\"say \\\"hi\\\"\"",
            "\"Na=me\\\\x\"=\"eq\"");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        var def = key.FindValue("")!;
        Assert.True(def.IsDefault);
        Assert.Equal("(Default)", def.DisplayName);
        Assert.Equal("default text", def.DisplayValue);
        Assert.Equal(@"C:\Program Files\App", key.FindValue("Path")!.DisplayValue);
        Assert.Equal("say \"hi\"", key.FindValue("Quote")!.DisplayValue);
        Assert.Equal("eq", key.FindValue(@"Na=me\x")!.DisplayValue);
        Assert.Equal("REG_SZ", def.TypeName);
    }

    [Fact]
    public void Dword_and_qword_values_show_hex_and_decimal()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\K]", "\"D\"=dword:000000ff", "\"Q\"=hex(b):01,00,00,00,00,00,00,00", "\"Q2\"=qword:0000000100000000");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal("0x000000ff (255)", key.FindValue("D")!.DisplayValue);
        Assert.Equal("REG_DWORD", key.FindValue("D")!.TypeName);
        Assert.Equal("0x0000000000000001 (1)", key.FindValue("Q")!.DisplayValue);
        Assert.Equal(RegistryValueKind.QWord, key.FindValue("Q")!.Kind);
        Assert.Equal("0x0000000100000000 (4294967296)", key.FindValue("Q2")!.DisplayValue);
    }

    [Fact]
    public void Hex_dword_forms_decode_little_and_big_endian()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\K]", "\"LE\"=hex(4):01,00,00,00", "\"BE\"=hex(5):00,00,00,01");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal("0x00000001 (1)", key.FindValue("LE")!.DisplayValue);
        Assert.Equal("0x00000001 (1)", key.FindValue("BE")!.DisplayValue);
        Assert.Equal("REG_DWORD_BIG_ENDIAN", key.FindValue("BE")!.TypeName);
    }

    [Fact]
    public void Utf16_strings_expand_strings_and_multi_strings_are_decoded()
    {
        static string Hex(string text, bool terminate = true) =>
            string.Join(",", Encoding.Unicode.GetBytes(text + (terminate ? "\0" : "")).Select(b => b.ToString("x2")));

        var doc = Parse(
            Header, @"[HKEY_CURRENT_USER\K]",
            $"\"Sz\"=hex(1):{Hex("héllo")}",
            $"\"Exp\"=hex(2):{Hex("%SystemRoot%\\system32")}",
            $"\"Multi\"=hex(7):{Hex("one\0two\0three\0")}");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal("héllo", key.FindValue("Sz")!.DisplayValue);
        Assert.Equal("REG_EXPAND_SZ", key.FindValue("Exp")!.TypeName);
        Assert.Equal(@"%SystemRoot%\system32", key.FindValue("Exp")!.DisplayValue);
        Assert.Equal("one | two | three", key.FindValue("Multi")!.DisplayValue);
        Assert.Equal("REG_MULTI_SZ", key.FindValue("Multi")!.TypeName);
    }

    [Fact]
    public void Binary_values_show_hex_and_keep_bytes_for_the_detail_pane()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\K]", "\"Bin\"=hex:de,ad,be,ef", "\"Empty\"=hex:", "\"None\"=hex(0):01");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        var bin = key.FindValue("Bin")!;
        Assert.Equal("REG_BINARY", bin.TypeName);
        Assert.Equal("DE AD BE EF", bin.DisplayValue);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, bin.Data);
        Assert.Equal(4, bin.DataLength);
        Assert.Equal(0, key.FindValue("Empty")!.DataLength);
        Assert.Equal("REG_NONE", key.FindValue("None")!.TypeName);
    }

    [Fact]
    public void Hex_values_continue_across_lines_ending_in_a_backslash()
    {
        var doc = Parse(
            Header, @"[HKEY_CURRENT_USER\K]",
            "\"Bin\"=hex:01,02,03,\\",
            "  04,05,06,\\",
            "  07",
            "\"After\"=\"x\"");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal(7, key.FindValue("Bin")!.DataLength);
        Assert.Equal("x", key.FindValue("After")!.DisplayValue);
        Assert.Equal(3, key.FindValue("Bin")!.SourceLine); // line numbers count physical lines
        Assert.Equal(6, key.FindValue("After")!.SourceLine);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void A_string_ending_in_an_escaped_backslash_is_not_treated_as_a_continuation()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\K]", "\"Dir\"=\"C:\\\\\"", "\"Next\"=\"y\"");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal(@"C:\", key.FindValue("Dir")!.DisplayValue);
        Assert.Equal("y", key.FindValue("Next")!.DisplayValue);
    }

    [Fact]
    public void Long_binary_values_are_abbreviated_for_display_and_bounded_in_memory()
    {
        var bytes = string.Join(",", Enumerable.Range(0, 10_000).Select(i => (i % 256).ToString("x2")));
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\K]", $"\"Big\"=hex:{bytes}");

        var value = doc.FindKey(@"HKEY_CURRENT_USER\K")!.FindValue("Big")!;
        Assert.Equal(10_000, value.DataLength);
        Assert.Contains("10,000 bytes", value.DisplayValue);
        Assert.Equal(RegistryValue.MaxStoredDataBytes, value.Data!.Length);
        Assert.True(value.IsDataTruncated);
    }

    [Fact]
    public void Unknown_hex_types_are_labelled_not_guessed()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\K]", "\"X\"=hex(ff):01,02");

        var value = doc.FindKey(@"HKEY_CURRENT_USER\K")!.FindValue("X")!;
        Assert.Equal(RegistryValueKind.Unknown, value.Kind);
        Assert.Equal("REG_UNKNOWN (0xff)", value.TypeName);
        Assert.Equal("01 02", value.DisplayValue);
    }

    // ---- malformed input ----

    [Fact]
    public void Malformed_lines_become_issues_and_parsing_continues()
    {
        var doc = Parse(
            Header,
            "garbage line",
            "\"Orphan\"=\"before any key\"",
            @"[HKEY_CURRENT_USER\Broken",
            @"[HKEY_CURRENT_USER\K]",
            "\"Bad\"=dword:zzzz",
            "\"BadHex\"=hex:01,xx",
            "\"NoEquals\"",
            "\"Unterminated=\"x\"",
            "\"Good\"=\"fine\"");

        var key = doc.FindKey(@"HKEY_CURRENT_USER\K")!;
        Assert.Equal("fine", key.FindValue("Good")!.DisplayValue);
        Assert.Equal("zzzz", key.FindValue("Bad")!.DisplayValue.Replace("dword:", ""));
        Assert.Equal("(unparsed)", key.FindValue("BadHex")!.TypeName);
        Assert.True(doc.TotalIssueCount >= 6);
        Assert.Contains(doc.Issues, i => i.Line == 2 && i.Message.Contains("Unrecognised"));
        Assert.Contains(doc.Issues, i => i.Line == 3 && i.Message.Contains("before any key"));
        Assert.Contains(doc.Issues, i => i.Line == 4 && i.Message.Contains("closing"));
    }

    [Fact]
    public void Values_after_a_broken_key_header_are_not_attached_to_the_previous_key()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\Good]", @"[HKEY_CURRENT_USER\Broken", "\"X\"=\"1\"");

        Assert.Empty(doc.FindKey(@"HKEY_CURRENT_USER\Good")!.Values);
    }

    [Fact]
    public void Issue_list_is_capped_but_the_total_is_kept()
    {
        var lines = new[] { Header }.Concat(Enumerable.Repeat("nonsense", RegFileParser.MaxRetainedIssues + 50)).ToArray();

        var doc = Parse(lines);

        Assert.Equal(RegFileParser.MaxRetainedIssues, doc.Issues.Count);
        Assert.Equal(RegFileParser.MaxRetainedIssues + 50, doc.TotalIssueCount);
    }

    [Fact]
    public void Empty_input_yields_an_empty_document()
    {
        var doc = Parse(Array.Empty<string>());

        Assert.Empty(doc.Keys);
        Assert.Null(doc.FormatHeader);
        Assert.Null(doc.FindKey("anything"));
    }

    [Fact]
    public void Find_key_tolerates_slashes_and_missing_paths()
    {
        var doc = Parse(Header, @"[HKEY_CURRENT_USER\A\B]");

        Assert.NotNull(doc.FindKey(@"\HKEY_CURRENT_USER\A\B\"));
        Assert.Null(doc.FindKey(@"HKEY_CURRENT_USER\A\Nope"));
        Assert.Null(doc.FindKey(""));
    }

    // ---- files ----

    [Fact]
    public async Task Parses_a_utf16_le_file_with_bom_and_crlf_and_provides_the_raw_source()
    {
        var text = string.Join(
            "\r\n",
            Header, "", @"[HKEY_LOCAL_MACHINE\SOFTWARE\Test]", "\"Name\"=\"Ünïcode ✓\"", "\"Num\"=dword:0000002a", "");
        var path = Path.Combine(_dir, "test.reg");
        File.WriteAllBytes(path, new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray());
        var artifact = Artifact with { ExtractedPath = path };

        var parser = new RegFileParser();
        Assert.True(parser.CanHandle(artifact));
        var doc = Assert.IsType<RegistryDocument>(await parser.ParseAsync(artifact, CancellationToken.None));

        var key = doc.FindKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Test")!;
        Assert.Equal("Ünïcode ✓", key.FindValue("Name")!.DisplayValue);
        Assert.Equal("0x0000002a (42)", key.FindValue("Num")!.DisplayValue);
        Assert.Equal(4, key.FindValue("Name")!.SourceLine);
        Assert.Equal("UTF-16 LE", doc.RawSource.EncodingName);
        Assert.Equal(Header, doc.RawSource.ReadLines(0, 1)[0]);
    }

    [Fact]
    public async Task Very_long_hex_lines_are_not_truncated_by_the_display_line_cap()
    {
        var bytes = string.Join(",", Enumerable.Range(0, 20_000).Select(i => (i % 256).ToString("x2"))); // ~60k chars
        var path = Path.Combine(_dir, "long.reg");
        File.WriteAllText(path, $"{Header}\n[HKEY_CURRENT_USER\\K]\n\"Big\"=hex:{bytes}\n");
        var artifact = Artifact with { ExtractedPath = path };

        var doc = (RegistryDocument)await new RegFileParser().ParseAsync(artifact, CancellationToken.None);

        Assert.Equal(20_000, doc.FindKey(@"HKEY_CURRENT_USER\K")!.FindValue("Big")!.DataLength);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void Parser_only_handles_registry_exports()
    {
        var parser = new RegFileParser();

        Assert.False(parser.CanHandle(Artifact with { ArtifactType = ArtifactType.TextLog, ExtractedPath = "x" }));
        Assert.False(parser.CanHandle(Artifact with { ExtractedPath = null }));
    }
}
