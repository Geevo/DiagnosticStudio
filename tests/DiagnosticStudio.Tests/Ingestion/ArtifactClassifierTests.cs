using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

public class ArtifactClassifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-classifier-" + Guid.NewGuid().ToString("N"));

    public ArtifactClassifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ArtifactClassification Classify(string name, byte[] content, params string[] folders)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        File.WriteAllBytes(path, content);
        return ArtifactClassifier.Classify(name, folders.Append(name).ToArray(), path);
    }

    private static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("CBS.log", "Windows Servicing", "CBS")]
    [InlineData("IntuneManagementExtension.log", "Intune", "IntuneManagementExtension")]
    [InlineData("AgentExecutor.log", "Intune", "AgentExecutor")]
    [InlineData("WindowsUpdate.log", "Windows Update", "WindowsUpdate")]
    public void Known_text_logs_get_category_and_subtype(string name, string category, string subtype)
    {
        var result = Classify(name, Text("2026-07-23 line"));

        Assert.Equal(ArtifactType.TextLog, result.Type);
        Assert.Equal(category, result.Category);
        Assert.Equal(subtype, result.Subtype);
    }

    [Theory]
    [InlineData("package.mum", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<assembly/>")]
    [InlineData("app.manifest", "  \r\n<?xml version=\"1.0\"?><a/>")]
    [InlineData("settings", "<?xml version='1.0'?><a/>")]
    [InlineData("web.config", "<?xml version=\"1.0\"?><configuration/>")]
    public void Xml_is_recognised_by_its_declaration_whatever_the_extension(string name, string content)
    {
        Assert.Equal(ArtifactType.Xml, Classify(name, Text(content)).Type);
    }

    [Fact]
    public void Xml_in_utf16_or_with_a_byte_order_mark_is_recognised()
    {
        var utf16 = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?><a/>")).ToArray();
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Text("<?xml version=\"1.0\"?><a/>")).ToArray();

        Assert.Equal(ArtifactType.Xml, Classify("a.mum", utf16).Type);
        Assert.Equal(ArtifactType.Xml, Classify("b.mum", utf8Bom).Type);
    }

    [Theory]
    [InlineData("agent.log")]
    [InlineData("notes.txt")]
    public void A_file_explicitly_named_as_text_stays_text_even_if_it_starts_with_an_xml_declaration(string name)
    {
        Assert.Equal(ArtifactType.TextLog, Classify(name, Text("<?xml version=\"1.0\"?><a/>")).Type);
    }

    [Theory]
    [InlineData("<html><body/></html>")]
    [InlineData("<xmlish/>")]
    [InlineData("plain text mentioning <?xml later")]
    public void Other_text_is_not_mistaken_for_xml(string content)
    {
        Assert.NotEqual(ArtifactType.Xml, Classify("thing.mum", Text(content)).Type);
    }

    [Theory]
    [InlineData("events.jsonl")]
    [InlineData("events.ndjson")]
    [InlineData("settings.json")]
    public void Json_and_json_lines_are_classified_as_json(string name)
    {
        Assert.Equal(ArtifactType.Json, Classify(name, Text("{\"a\":1}")).Type);
    }

    [Fact]
    public void Command_capture_requires_output_marker_or_command_folder()
    {
        var byName = Classify("ipconfig_all_output.log", Text("x"));
        var byFolder = Classify("ipconfig_all.txt", Text("x"), "Command");
        var plain = Classify("ipconfig_notes.txt", Text("x"));

        Assert.Equal(ArtifactType.CommandOutput, byName.Type);
        Assert.Equal(ArtifactType.CommandOutput, byFolder.Type);
        Assert.Equal(ArtifactType.TextLog, plain.Type);
        Assert.Equal("Networking", byName.Category);
        Assert.Equal("IpConfig", byName.Subtype);
    }

    [Fact]
    public void Content_sniffing_overrides_misleading_extensions()
    {
        var zipNamedLog = Classify("archive.log", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 });
        var evtxNamedTxt = Classify("System.txt", Text("ElfFile\0") );

        Assert.Equal(ArtifactType.Archive, zipNamedLog.Type);
        Assert.Equal(ArtifactType.EventLog, evtxNamedTxt.Type);
    }

    [Fact]
    public void Binary_content_without_a_known_extension_is_not_treated_as_text()
    {
        var result = Classify("mystery", new byte[] { 0, 1, 2, 3, 0, 5, 6, 0, 0, 1 });

        Assert.Equal(ArtifactType.Binary, result.Type);
    }

    [Fact]
    public void Utf16_text_with_bom_is_text()
    {
        var content = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("hello")).ToArray();

        Assert.Equal(ArtifactType.TextLog, Classify("unicode.log", content).Type);
    }

    [Fact]
    public void Utf16_text_without_bom_is_text()
    {
        Assert.Equal(ArtifactType.TextLog, Classify("unicode.log", System.Text.Encoding.Unicode.GetBytes("hello world")).Type);
    }

    [Fact]
    public void Empty_files_are_text()
    {
        Assert.Equal(ArtifactType.TextLog, Classify("empty.log", Array.Empty<byte>()).Type);
    }
}
