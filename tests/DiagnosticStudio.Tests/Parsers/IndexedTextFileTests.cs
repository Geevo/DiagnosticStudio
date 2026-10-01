using System.Text;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.Parsers;

public sealed class IndexedTextFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-text-" + Guid.NewGuid().ToString("N"));

    public IndexedTextFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(byte[] content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllBytes(path, content);
        return path;
    }

    private string Write(string text, Encoding? encoding = null) =>
        Write((encoding ?? new UTF8Encoding(false)).GetBytes(text));

    private static string[] All(IndexedTextFile file) => file.EnumerateLines().ToArray();

    [Fact]
    public void Empty_file_has_no_lines()
    {
        var file = IndexedTextFile.Open(Write(Array.Empty<byte>()));

        Assert.Equal(0, file.LineCount);
        Assert.Empty(file.ReadLines(0, 10));
    }

    [Theory]
    [InlineData("one\ntwo\nthree\n")]
    [InlineData("one\ntwo\nthree")]
    [InlineData("one\r\ntwo\r\nthree\r\n")]
    [InlineData("one\r\ntwo\nthree")]
    public void Lines_are_split_on_lf_and_crlf_with_or_without_trailing_newline(string text)
    {
        var file = IndexedTextFile.Open(Write(text));

        Assert.Equal(3, file.LineCount);
        Assert.Equal(new[] { "one", "two", "three" }, All(file));
    }

    [Fact]
    public void Blank_lines_are_kept()
    {
        var file = IndexedTextFile.Open(Write("a\n\nb\n\n"));

        Assert.Equal(new[] { "a", "", "b", "" }, All(file));
        Assert.Equal(4, file.LineCount);
    }

    [Fact]
    public void Single_newline_is_one_empty_line()
    {
        var file = IndexedTextFile.Open(Write("\n"));

        Assert.Equal(1, file.LineCount);
        Assert.Equal(new[] { "" }, All(file));
    }

    [Fact]
    public void Random_access_matches_sequential_reading_across_checkpoints()
    {
        var expected = Enumerable.Range(1, 1000).Select(i => $"line {i} " + new string('x', i % 17)).ToArray();
        var file = IndexedTextFile.Open(Write(string.Join("\n", expected) + "\n"));

        Assert.Equal(1000, file.LineCount);
        Assert.Equal(expected, All(file));

        foreach (var start in new[] { 0, 1, 127, 128, 129, 255, 256, 700, 999 })
        {
            var page = file.ReadLines(start, 40);
            Assert.Equal(expected.Skip(start).Take(40), page);
        }

        Assert.Empty(file.ReadLines(1000, 5));
    }

    [Fact]
    public void Tiny_read_buffers_do_not_change_results()
    {
        var expected = Enumerable.Range(1, 500).Select(i => new string((char)('a' + (i % 26)), (i % 50) + 1)).ToArray();
        var path = Write(string.Join("\r\n", expected));

        var file = IndexedTextFile.Open(path, bufferSize: 7);

        Assert.Equal(expected.Length, file.LineCount);
        Assert.Equal(expected, All(file));
        Assert.Equal(expected.Skip(300).Take(10), file.ReadLines(300, 10));
    }

    [Fact]
    public void Utf8_bom_is_skipped_and_reported()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("héllo\nwörld")).ToArray();
        var file = IndexedTextFile.Open(Write(bytes));

        Assert.Equal("UTF-8 (BOM)", file.EncodingName);
        Assert.Equal(new[] { "héllo", "wörld" }, All(file));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Utf16_is_decoded_with_or_without_bom(bool bigEndian, bool withBom)
    {
        var encoding = new UnicodeEncoding(bigEndian, withBom);
        var text = string.Join("\r\n", Enumerable.Range(0, 300).Select(i => $"entry {i} ünï"));
        var bytes = (withBom ? encoding.GetPreamble() : Array.Empty<byte>()).Concat(encoding.GetBytes(text)).ToArray();

        var file = IndexedTextFile.Open(Write(bytes), bufferSize: 11);

        Assert.StartsWith("UTF-16", file.EncodingName);
        Assert.Equal(300, file.LineCount);
        Assert.Equal("entry 0 ünï", file.ReadLines(0, 1)[0]);
        Assert.Equal("entry 257 ünï", file.ReadLines(257, 1)[0]);
    }

    [Fact]
    public void Very_long_lines_are_truncated_for_display()
    {
        var file = IndexedTextFile.Open(Write(new string('a', 100_000) + "\nshort\n"), maxLineChars: 1000);

        var lines = All(file);

        Assert.Equal(2, lines.Length);
        Assert.Equal(1000 + IndexedTextFile.TruncationMarker.Length, lines[0].Length);
        Assert.EndsWith(IndexedTextFile.TruncationMarker, lines[0]);
        Assert.Equal("short", lines[1]);
    }

    [Fact]
    public void Invalid_utf8_still_opens()
    {
        var file = IndexedTextFile.Open(Write(new byte[] { 0x61, 0xFF, 0xFE, 0x62, 0x0A, 0x63 }));

        Assert.Equal(2, file.LineCount);
        Assert.Equal("c", file.ReadLines(1, 1)[0]);
    }

    [Fact]
    public void File_remains_deletable_between_reads()
    {
        var path = Write("a\nb\n");
        var file = IndexedTextFile.Open(path);
        _ = file.ReadLines(0, 1);

        File.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Opening_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IndexedTextFile.OpenAsync(Write("a\nb\n"), cts.Token));
    }
}
