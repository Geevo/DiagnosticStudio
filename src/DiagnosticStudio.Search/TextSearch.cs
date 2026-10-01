using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Search;

/// <param name="Lines">One-based numbers of matching lines, ascending.</param>
/// <param name="Truncated">True when scanning stopped at the match cap.</param>
public sealed record TextSearchResult(IReadOnlyList<int> Lines, bool Truncated)
{
    public static TextSearchResult Empty { get; } = new(Array.Empty<int>(), false);
}

/// <summary>Within-document search over an <see cref="ITextLineSource"/>.</summary>
public static class TextSearch
{
    public const int DefaultMaxMatches = 100_000;

    /// <summary>Finds lines containing <paramref name="query"/> (plain text, not a pattern).</summary>
    public static Task<TextSearchResult> FindLinesAsync(
        ITextLineSource source,
        string query,
        bool matchCase,
        CancellationToken cancellationToken,
        int maxMatches = DefaultMaxMatches) =>
        Task.Run(() => FindLines(source, query, matchCase, cancellationToken, maxMatches), cancellationToken);

    public static TextSearchResult FindLines(
        ITextLineSource source,
        string query,
        bool matchCase,
        CancellationToken cancellationToken,
        int maxMatches = DefaultMaxMatches)
    {
        if (string.IsNullOrEmpty(query))
        {
            return TextSearchResult.Empty;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<int>();
        var lineNumber = 0;

        foreach (var line in source.EnumerateLines())
        {
            lineNumber++;
            if ((lineNumber & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!line.Contains(query, comparison))
            {
                continue;
            }

            if (matches.Count == maxMatches)
            {
                return new TextSearchResult(matches, Truncated: true);
            }

            matches.Add(lineNumber);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new TextSearchResult(matches, Truncated: false);
    }
}
