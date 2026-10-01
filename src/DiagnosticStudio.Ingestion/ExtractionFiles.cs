namespace DiagnosticStudio.Ingestion;

/// <summary>File-system helpers shared by the archive providers.</summary>
internal static class ExtractionFiles
{
    /// <summary>Returns <paramref name="path"/>, or a "name (2).ext" variant when an earlier entry already used it.</summary>
    public static string MakeUnique(string path, HashSet<string> used)
    {
        if (used.Add(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
