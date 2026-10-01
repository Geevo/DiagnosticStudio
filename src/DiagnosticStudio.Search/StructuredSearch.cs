using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Search;

public enum StructuredMatchField
{
    Name,
    Value,
}

public sealed record StructuredMatch(StructuredNode Node, StructuredMatchField Field);

public sealed record StructuredSearchResult(IReadOnlyList<StructuredMatch> Matches, bool Truncated)
{
    public static StructuredSearchResult Empty { get; } = new(Array.Empty<StructuredMatch>(), false);
}

/// <summary>Within-document search over the names and values of an XML or JSON tree, in document order.</summary>
public static class StructuredSearch
{
    public const int DefaultMaxMatches = 100_000;

    public static Task<StructuredSearchResult> FindAsync(
        StructuredDocument document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken,
        int maxMatches = DefaultMaxMatches) =>
        Task.Run(() => Find(document, query, matchCase, cancellationToken, maxMatches), cancellationToken);

    public static StructuredSearchResult Find(
        StructuredDocument document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken,
        int maxMatches = DefaultMaxMatches)
    {
        if (string.IsNullOrEmpty(query))
        {
            return StructuredSearchResult.Empty;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<StructuredMatch>();
        var visited = 0;

        // Explicit stack: document order without recursion, so deep trees are safe.
        var stack = new Stack<StructuredNode>();
        stack.Push(document.Root);
        while (stack.Count > 0)
        {
            if ((++visited & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var node = stack.Pop();
            if (node.Name is { Length: > 0 } name && name.Contains(query, comparison))
            {
                if (matches.Count >= maxMatches)
                {
                    return new StructuredSearchResult(matches, Truncated: true);
                }

                matches.Add(new StructuredMatch(node, StructuredMatchField.Name));
            }

            if (node.Value is { Length: > 0 } value && value.Contains(query, comparison))
            {
                if (matches.Count >= maxMatches)
                {
                    return new StructuredSearchResult(matches, Truncated: true);
                }

                matches.Add(new StructuredMatch(node, StructuredMatchField.Value));
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return new StructuredSearchResult(matches, Truncated: false);
    }
}
