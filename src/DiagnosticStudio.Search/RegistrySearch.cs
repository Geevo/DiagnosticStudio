using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Search;

public enum RegistryMatchField
{
    KeyPath,
    ValueName,
    ValueData,
}

/// <param name="Value">The matching value, or <c>null</c> when the key itself matched.</param>
public sealed record RegistryMatch(RegistryKey Key, RegistryValue? Value, RegistryMatchField Field);

public sealed record RegistrySearchResult(IReadOnlyList<RegistryMatch> Matches, bool Truncated)
{
    public static RegistrySearchResult Empty { get; } = new(Array.Empty<RegistryMatch>(), false);
}

/// <summary>
/// Within-document search over key names/paths, value names and value data.
/// A query containing a backslash is matched against the full key path; otherwise against the key's own name,
/// so searching for "Software" does not match every descendant of a "Software" key.
/// </summary>
public static class RegistrySearch
{
    public const int DefaultMaxMatches = 100_000;

    public static Task<RegistrySearchResult> FindAsync(
        RegistryDocument document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken,
        int maxMatches = DefaultMaxMatches) =>
        Task.Run(() => Find(document, query, matchCase, cancellationToken, maxMatches), cancellationToken);

    public static RegistrySearchResult Find(
        RegistryDocument document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken,
        int maxMatches = DefaultMaxMatches)
    {
        if (string.IsNullOrEmpty(query))
        {
            return RegistrySearchResult.Empty;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matchFullPath = query.Contains('\\');
        var matches = new List<RegistryMatch>();
        var visited = 0;

        foreach (var key in document.Keys)
        {
            if ((++visited & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var keyText = matchFullPath ? key.FullPath : key.Name;
            if (keyText.Contains(query, comparison) && !Add(matches, new RegistryMatch(key, null, RegistryMatchField.KeyPath), maxMatches))
            {
                return new RegistrySearchResult(matches, Truncated: true);
            }

            foreach (var value in key.Values)
            {
                RegistryMatchField? field = null;
                if (value.Name.Contains(query, comparison) || (value.IsDefault && "(Default)".Contains(query, comparison)))
                {
                    field = RegistryMatchField.ValueName;
                }
                else if (!value.IsDeleted && value.DisplayValue.Contains(query, comparison))
                {
                    field = RegistryMatchField.ValueData;
                }

                if (field is { } f && !Add(matches, new RegistryMatch(key, value, f), maxMatches))
                {
                    return new RegistrySearchResult(matches, Truncated: true);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RegistrySearchResult(matches, Truncated: false);
    }

    private static bool Add(List<RegistryMatch> matches, RegistryMatch match, int max)
    {
        if (matches.Count >= max)
        {
            return false;
        }

        matches.Add(match);
        return true;
    }
}
