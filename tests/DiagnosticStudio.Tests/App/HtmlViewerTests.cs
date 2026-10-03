using System.Text;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.HtmlViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Parsers;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public sealed class HtmlViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-html-" + Guid.NewGuid().ToString("N"));

    public HtmlViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ======================= the sandbox =======================

    private static string Policy(string prepared)
    {
        var start = prepared.IndexOf("content=\"", StringComparison.Ordinal) + "content=\"".Length;
        return prepared[start..prepared.IndexOf('"', start)];
    }

    [Fact]
    public void The_policy_is_the_first_thing_in_a_page_without_a_doctype()
    {
        var prepared = HtmlSandbox.Prepare("<html><head><script>alert(1)</script></head><body>x</body></html>");

        Assert.StartsWith("<meta http-equiv=\"Content-Security-Policy\"", prepared);
        Assert.True(
            prepared.IndexOf("Content-Security-Policy", StringComparison.Ordinal) < prepared.IndexOf("<script>", StringComparison.Ordinal),
            "the policy must come before anything the page can run");
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>x</body></html>")]
    [InlineData("  \r\n<!doctype HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\">\r\n<html></html>")]
    public void A_doctype_stays_first_so_the_page_keeps_its_layout_mode(string html)
    {
        var prepared = HtmlSandbox.Prepare(html);

        var doctypeAt = prepared.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
        var policyAt = prepared.IndexOf("Content-Security-Policy", StringComparison.Ordinal);
        Assert.True(doctypeAt >= 0 && doctypeAt < policyAt);
        Assert.Equal(0, prepared.TrimStart().IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nothing_of_the_page_is_lost_or_changed()
    {
        var html = "<!DOCTYPE html>\n<html><body><h1>T\u00e9st</h1><script>var a = 1;</script></body></html>";

        var prepared = HtmlSandbox.Prepare(html);

        Assert.Contains("<h1>T\u00e9st</h1><script>var a = 1;</script></body></html>", prepared);
        Assert.StartsWith("<!DOCTYPE html>", prepared);
    }

    [Fact]
    public void The_policy_forbids_scripts_network_frames_forms_and_a_base_address()
    {
        var policy = Policy(HtmlSandbox.Prepare("<p>x</p>"));

        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("script-src 'none'", policy);
        Assert.Contains("connect-src 'none'", policy);
        Assert.Contains("frame-src 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.Contains("base-uri 'none'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public void The_policy_allows_only_what_cannot_reach_out()
    {
        var policy = Policy(HtmlSandbox.Prepare("<p>x</p>"));

        // Images and fonts only as data inside the page; styles only inline.
        Assert.Contains("img-src data:", policy);
        Assert.Contains("font-src data:", policy);
        Assert.Contains("style-src 'unsafe-inline'", policy);
        Assert.DoesNotContain("http", policy);
        Assert.DoesNotContain("*", policy);
        Assert.DoesNotContain("script-src 'unsafe", policy);
        Assert.DoesNotContain("'self'", policy);
    }

    [Fact]
    public void A_light_base_style_comes_before_the_pages_own_so_that_theirs_still_win()
    {
        var prepared = HtmlSandbox.Prepare("<html><head><style>html{background:#123}</style></head></html>");

        Assert.True(
            prepared.IndexOf("background-color:#ffffff", StringComparison.Ordinal) < prepared.IndexOf("background:#123", StringComparison.Ordinal));
    }

    [Fact]
    public void Scripts_are_off_unless_asked_for()
    {
        Assert.Contains("script-src 'none'", Policy(HtmlSandbox.Prepare("<p>x</p>")));
        Assert.Contains("script-src 'none'", Policy(HtmlSandbox.Prepare("<p>x</p>", allowScripts: false)));
    }

    [Fact]
    public void With_scripts_allowed_only_inline_scripts_run_and_everything_else_stays_shut()
    {
        var policy = Policy(HtmlSandbox.Prepare("<p>x</p>", allowScripts: true));

        Assert.Contains("script-src 'unsafe-inline'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.DoesNotContain("http", policy);
        Assert.DoesNotContain("*", policy);
        Assert.DoesNotContain("'self'", policy);
        foreach (var shut in new[] { "default-src 'none'", "connect-src 'none'", "frame-src 'none'", "object-src 'none'", "form-action 'none'", "base-uri 'none'", "frame-ancestors 'none'", "img-src data:", "font-src data:" })
        {
            Assert.Contains(shut, policy);
        }
    }

    [Fact]
    public void Allowing_scripts_still_puts_the_policy_first_and_keeps_the_doctype_first()
    {
        var prepared = HtmlSandbox.Prepare("<!DOCTYPE html><html><script>x()</script></html>", allowScripts: true);

        Assert.StartsWith("<!DOCTYPE html><meta http-equiv=\"Content-Security-Policy\"", prepared);
        Assert.True(prepared.IndexOf("Content-Security-Policy", StringComparison.Ordinal) < prepared.IndexOf("<script>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("data:text/html;charset=utf-8,hello", true)]
    [InlineData("DATA:image/png;base64,AAAA", true)]
    [InlineData("about:blank", true)]
    [InlineData("http://127.0.0.1:8765/probe.png", false)]
    [InlineData("https://example.com/x.css", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ftp://host/file", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("//example.com/protocol-relative", false)]
    [InlineData("blob:https://example.com/abc", false)]
    [InlineData("", false)]
    public void Only_data_inside_the_page_is_loaded_and_everything_else_is_refused(string uri, bool allowed)
    {
        Assert.Equal(allowed, HtmlSandbox.IsAllowedRequest(uri));
    }

    // ======================= the parser =======================

    private DiagnosticArtifact HtmlArtifact(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = ArtifactType.Html,
        };
    }

    [Fact]
    public async Task A_small_page_keeps_its_markup_for_drawing_and_its_text_for_the_source_tab()
    {
        var artifact = HtmlArtifact("report.html", Encoding.UTF8.GetBytes("<html><body>Hello</body></html>\r\n<!-- end -->\r\n"));
        var parser = new HtmlFileParser();

        Assert.True(parser.CanHandle(artifact));
        var document = Assert.IsType<HtmlDocument>(await parser.ParseAsync(artifact, CancellationToken.None));

        Assert.Equal("<html><body>Hello</body></html>\r\n<!-- end -->\r\n", document.Markup);
        Assert.Null(document.RenderNote);
        Assert.Equal(2, document.RawSource.LineCount);
    }

    [Fact]
    public async Task A_page_too_large_to_draw_keeps_only_its_source_and_says_why()
    {
        var big = new string('x', (int)HtmlDocument.MaxRenderBytes + 100);
        var artifact = HtmlArtifact("big.html", Encoding.UTF8.GetBytes("<html><body>" + big + "</body></html>"));

        var document = (HtmlDocument)await new HtmlFileParser().ParseAsync(artifact, CancellationToken.None);

        Assert.Null(document.Markup);
        Assert.Contains("larger than the 1.8 MB that is rendered", document.RenderNote);
        Assert.True(document.RawSource.ByteLength > HtmlDocument.MaxRenderBytes);
    }

    [Fact]
    public void Text_is_decoded_by_its_byte_order_mark_then_as_utf8_then_as_latin1()
    {
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("caf\u00e9")).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("caf\u00e9")).ToArray();
        var utf16Be = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("caf\u00e9")).ToArray();
        var utf8 = Encoding.UTF8.GetBytes("caf\u00e9");
        var latin1 = new byte[] { 0x63, 0x61, 0x66, 0xE9 };

        Assert.Equal("caf\u00e9", HtmlFileParser.Decode(utf8Bom));
        Assert.Equal("caf\u00e9", HtmlFileParser.Decode(utf16));
        Assert.Equal("caf\u00e9", HtmlFileParser.Decode(utf16Be));
        Assert.Equal("caf\u00e9", HtmlFileParser.Decode(utf8));
        Assert.Equal("caf\u00e9", HtmlFileParser.Decode(latin1)); // not valid UTF-8, so Latin-1
        Assert.Equal(string.Empty, HtmlFileParser.Decode(Array.Empty<byte>()));
    }

    [Fact]
    public void Only_html_artifacts_with_a_file_are_taken()
    {
        var artifact = HtmlArtifact("a.html", new byte[] { 65 });

        Assert.False(new HtmlFileParser().CanHandle(artifact with { ArtifactType = ArtifactType.TextLog }));
        Assert.False(new HtmlFileParser().CanHandle(artifact with { ExtractedPath = null }));
    }

    [Fact]
    public void A_pages_text_is_its_source()
    {
        var artifact = Artifact("a.html", ArtifactType.Html);
        var document = new HtmlDocument { Artifact = artifact, RawSource = new ListLines(new[] { "<p>x</p>" }), Markup = "<p>x</p>" };

        Assert.Same(document.RawSource, DocumentText.LinesOf(document));
    }

    // ======================= the viewer =======================

    private static HtmlViewerViewModel Viewer(string? markup = "<p>x</p>", string? note = null, params string[] lines)
    {
        var artifact = Artifact("report.html", ArtifactType.Html);
        var document = new HtmlDocument
        {
            Artifact = artifact,
            RawSource = new ListLines(lines.Length == 0 ? new[] { "<p>x</p>" } : lines),
            Markup = markup,
            RenderNote = note,
        };
        return new HtmlViewerViewModel(document);
    }

    [Fact]
    public void A_page_that_can_be_drawn_opens_on_the_page()
    {
        var vm = Viewer();

        Assert.True(vm.CanRender);
        Assert.False(vm.HasRenderError);
        Assert.Equal(HtmlViewerViewModel.PageTab, vm.SelectedTabIndex);
        Assert.StartsWith("HTML · 1 lines", vm.InfoText);
    }

    [Fact]
    public void A_page_too_large_to_draw_opens_on_the_source_with_the_reason()
    {
        var vm = Viewer(markup: null, note: "This file is 5.0 MB, larger than the 1.8 MB that is rendered. Its source is shown.");

        Assert.False(vm.CanRender);
        Assert.True(vm.HasRenderError);
        Assert.Contains("larger than", vm.RenderError);
        Assert.Equal(HtmlViewerViewModel.SourceTab, vm.SelectedTabIndex);
    }

    [Fact]
    public void When_the_browser_component_fails_the_source_is_shown_and_the_reason_is_given()
    {
        var vm = Viewer();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ReportRenderFailure("The Microsoft Edge WebView2 component is not installed, so the page cannot be drawn. The source is shown.");

        Assert.False(vm.CanRender);
        Assert.Equal(HtmlViewerViewModel.SourceTab, vm.SelectedTabIndex);
        Assert.Contains("WebView2", vm.RenderError);
        Assert.Contains(nameof(HtmlViewerViewModel.CanRender), changed);
        Assert.Contains(nameof(HtmlViewerViewModel.HasRenderError), changed);
    }

    [Fact]
    public void A_line_link_shows_the_source_at_that_line_and_other_links_are_not_understood()
    {
        var vm = Viewer(lines: new[] { "<html>", "<body>", "<p>needle</p>", "</body>" });

        Assert.True(vm.NavigateTo(DiagnosticLocation.ForLine(Guid.NewGuid(), 3)));

        Assert.Equal(HtmlViewerViewModel.SourceTab, vm.SelectedTabIndex);
        Assert.Equal(3, vm.Raw.CurrentLine);
        Assert.False(vm.NavigateTo(DiagnosticLocation.ForEventRecord(Guid.NewGuid(), 3)));
    }

    [Fact]
    public void A_search_highlight_goes_to_the_source()
    {
        var vm = Viewer();

        vm.Highlight(new SearchHighlight("needle", MatchCase: true));

        Assert.Equal("needle", vm.Raw.FindText);
        Assert.True(vm.Raw.MatchCase);
    }

    [Fact]
    public void Scripts_are_off_when_a_page_opens_and_the_note_changes_when_they_are_allowed()
    {
        var vm = Viewer();

        Assert.False(vm.AllowScripts);
        Assert.Equal(HtmlViewerViewModel.SandboxNote, vm.Note);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.AllowScripts = true;

        Assert.Equal(HtmlViewerViewModel.ScriptsNote, vm.Note);
        Assert.Contains(nameof(HtmlViewerViewModel.Note), changed);
        Assert.Contains("cannot send or fetch anything", HtmlViewerViewModel.ScriptsNote);
    }

    [Fact]
    public void A_new_viewer_never_inherits_the_choice()
    {
        var first = Viewer();
        first.AllowScripts = true;

        Assert.False(Viewer().AllowScripts);
    }
}
