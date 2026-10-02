using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.Parsers;

/// <summary>Exports that write the type as a word before the value: <c>"Name"=MULTI_SZ:"text"</c>, <c>DWORD:00000003</c>.</summary>
public sealed class RegFileParserToolTypedTests
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

    private const string Key = @"[HKEY_LOCAL_MACHINE\software\microsoft\provisioning\Results\{1}]";

    private static RegistryValue Value(string line, string name, out RegistryDocument doc)
    {
        doc = RegFileParser.Parse(Artifact, (Key + "\n" + line).Split('\n'), new NoSource());
        return doc.FindKey(@"HKEY_LOCAL_MACHINE\software\microsoft\provisioning\Results\{1}")!.FindValue(name)!;
    }

    [Fact]
    public void A_multi_string_written_with_its_type_word_is_read_as_one()
    {
        var value = Value("    \"Categories\"=MULTI_SZ:\"PowerSettings\"", "Categories", out var doc);

        Assert.Equal(RegistryValueKind.MultiString, value.Kind);
        Assert.Equal("REG_MULTI_SZ", value.TypeName);
        Assert.Equal("PowerSettings", value.DisplayValue);
        Assert.Empty(doc.Issues);
    }

    [Theory]
    [InlineData("EXPAND_SZ", RegistryValueKind.ExpandString)]
    [InlineData("REG_EXPAND_SZ", RegistryValueKind.ExpandString)]
    [InlineData("sz", RegistryValueKind.String)]
    [InlineData("REG_MULTI_SZ", RegistryValueKind.MultiString)]
    public void String_types_are_recognised_with_or_without_the_REG_prefix_in_any_case(string word, RegistryValueKind kind)
    {
        var value = Value("\"A\"=" + word + ":\"%SystemRoot%/x\"", "A", out var doc);

        Assert.Equal(kind, value.Kind);
        Assert.Equal("%SystemRoot%/x", value.DisplayValue);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void Binary_values_written_with_a_type_word_are_read_as_bytes()
    {
        var value = Value("\"B\"=BINARY:0A,0B,ff", "B", out var doc);

        Assert.Equal(RegistryValueKind.Binary, value.Kind);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void A_dword_written_with_a_type_word_still_works()
    {
        var value = Value("\"State\"=DWORD:00000003", "State", out var doc);

        Assert.Equal(RegistryValueKind.DWord, value.Kind);
        Assert.Empty(doc.Issues);
    }

    [Fact]
    public void An_unknown_word_is_still_reported_as_unrecognised()
    {
        Value("\"X\"=WIBBLE:\"1\"", "X", out var doc);

        Assert.Single(doc.Issues);
    }

    [Fact]
    public void Bad_hex_after_a_type_word_is_reported_and_the_line_is_kept()
    {
        var value = Value("\"B\"=BINARY:zz,0B", "B", out var doc);

        Assert.Single(doc.Issues);
        Assert.NotNull(value);
    }
}
