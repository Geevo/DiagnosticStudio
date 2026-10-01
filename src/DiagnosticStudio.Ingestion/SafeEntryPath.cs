namespace DiagnosticStudio.Ingestion;

/// <summary>
/// Maps untrusted archive entry names to paths guaranteed to stay inside a destination directory.
/// </summary>
public static class SafeEntryPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Splits an entry name into display segments (separators normalised, empty and '.' segments dropped).
    /// Returns <c>false</c> when the name is rooted or tries to traverse upwards.
    /// </summary>
    public static bool TrySplit(string entryName, out string[] segments, out string? rejectionReason)
    {
        segments = Array.Empty<string>();
        rejectionReason = null;

        if (string.IsNullOrWhiteSpace(entryName))
        {
            rejectionReason = "Entry name is empty.";
            return false;
        }

        var normalised = entryName.Replace('\\', '/');
        if (normalised.StartsWith('/'))
        {
            rejectionReason = "Entry uses an absolute path.";
            return false;
        }

        if (normalised.Length >= 2 && normalised[1] == ':' && char.IsLetter(normalised[0]))
        {
            rejectionReason = "Entry uses a drive-qualified path.";
            return false;
        }

        var parts = new List<string>();
        foreach (var raw in normalised.Split('/'))
        {
            var part = raw.TrimEnd(' ', '.');
            if (raw == "." || part.Length == 0 && raw.Length == 0)
            {
                continue;
            }

            // A segment made only of dots/spaces ("..", ". .", "...") is a traversal attempt, not a name.
            if (part.Length == 0)
            {
                rejectionReason = "Entry contains a path traversal segment.";
                return false;
            }

            parts.Add(raw.TrimEnd());
        }

        if (parts.Count == 0)
        {
            rejectionReason = "Entry name has no usable segments.";
            return false;
        }

        segments = parts.ToArray();
        return true;
    }

    /// <summary>
    /// Resolves an entry to an absolute path under <paramref name="destinationDirectory"/>, replacing characters
    /// Windows cannot store. Returns <c>false</c> (with a reason) for anything that could escape the destination.
    /// </summary>
    public static bool TryResolve(
        string destinationDirectory,
        string entryName,
        out string fullPath,
        out string? rejectionReason)
    {
        fullPath = string.Empty;
        if (!TrySplit(entryName, out var segments, out rejectionReason))
        {
            return false;
        }

        var safe = segments.Select(SanitiseSegment).ToArray();
        var root = Path.GetFullPath(destinationDirectory);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(safe)));
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "Entry resolves outside the extraction directory.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string SanitiseSegment(string segment)
    {
        var chars = segment.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0 || char.IsControl(chars[i]))
            {
                chars[i] = '_';
            }
        }

        var cleaned = new string(chars).TrimEnd(' ', '.');
        var stem = cleaned.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + cleaned : cleaned;
    }
}
