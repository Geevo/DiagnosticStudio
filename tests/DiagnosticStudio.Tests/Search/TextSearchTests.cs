using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.Tests.Search;

public class TextSearchTests
{
    private sealed class ListSource : ITextLineSource
    {
        private readonly string[] _lines;

        public ListSource(params string[] lines) => _lines = lines;

        public int LineCount => _lines.Length;
        public long ByteLength => 0;
        public string EncodingName => "test";

        public IReadOnlyList<string> ReadLines(int startLine, int count) => _lines.Skip(startLine).Take(count).ToArray();

        public IEnumerable<string> EnumerateLines(int startLine = 0) => _lines.Skip(startLine);
    }

    [Fact]
    public void Returns_one_based_line_numbers_of_matching_lines()
    {
        var source = new ListSource("alpha", "Beta needle", "gamma", "needle again", "x");

        var result = TextSearch.FindLines(source, "needle", matchCase: true, CancellationToken.None);

        Assert.Equal(new[] { 2, 4 }, result.Lines);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Case_sensitivity_is_respected()
    {
        var source = new ListSource("Error", "error", "ERROR");

        Assert.Equal(new[] { 2 }, TextSearch.FindLines(source, "error", true, CancellationToken.None).Lines);
        Assert.Equal(new[] { 1, 2, 3 }, TextSearch.FindLines(source, "error", false, CancellationToken.None).Lines);
    }

    [Fact]
    public void Query_is_plain_text_not_a_pattern()
    {
        var source = new ListSource("a.c", "abc", "0x80072F8F");

        Assert.Equal(new[] { 1 }, TextSearch.FindLines(source, "a.c", true, CancellationToken.None).Lines);
        Assert.Equal(new[] { 3 }, TextSearch.FindLines(source, "0x80072F8F", true, CancellationToken.None).Lines);
    }

    [Fact]
    public void Empty_query_matches_nothing()
    {
        Assert.Empty(TextSearch.FindLines(new ListSource("a"), "", true, CancellationToken.None).Lines);
    }

    [Fact]
    public void Match_cap_truncates_the_result()
    {
        var source = new ListSource(Enumerable.Repeat("hit", 10).ToArray());

        var result = TextSearch.FindLines(source, "hit", true, CancellationToken.None, maxMatches: 3);

        Assert.Equal(new[] { 1, 2, 3 }, result.Lines);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TextSearch.FindLinesAsync(new ListSource("a"), "a", true, cts.Token));
    }
}
