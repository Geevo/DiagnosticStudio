using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>The JSON form of a set of rules, used for the saved file and for import and export.</summary>
public static class CustomRuleJson
{
    public const int Schema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class FileShape
    {
        public int Schema { get; set; } = CustomRuleJson.Schema;

        public List<CustomRule> Rules { get; set; } = new();
    }

    public static string Serialize(IEnumerable<CustomRule> rules) =>
        JsonSerializer.Serialize(new FileShape { Rules = rules.ToList() }, Options);

    /// <summary>Reads rules from text. A file that is not valid JSON, or is for a newer format, is refused with a reason.</summary>
    public static bool TryParse(string text, out IReadOnlyList<CustomRule> rules, out string? error)
    {
        rules = Array.Empty<CustomRule>();
        error = null;
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(text, Options);
            if (shape is null)
            {
                error = "The file is empty.";
                return false;
            }

            if (shape.Schema > Schema)
            {
                error = $"The file is for a newer version of this application (format {shape.Schema}).";
                return false;
            }

            rules = GiveUniqueIds(shape.Rules.Where(r => r is not null));
            return true;
        }
        catch (JsonException ex)
        {
            error = "The file is not valid rule JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>Makes every rule's id non-empty and different from the others, keeping ids that already are.</summary>
    public static IReadOnlyList<CustomRule> GiveUniqueIds(IEnumerable<CustomRule> rules, IEnumerable<string>? taken = null)
    {
        var used = new HashSet<string>(taken ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<CustomRule>();
        foreach (var rule in rules)
        {
            var id = rule.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || used.Contains(id))
            {
                id = NewId(rule.Name, used);
            }

            used.Add(id);
            result.Add(rule with { Id = id });
        }

        return result;
    }

    public static string NewId(string? name, ISet<string> used)
    {
        var slug = new string((name ?? string.Empty).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length == 0)
        {
            slug = "rule";
        }

        var id = slug;
        for (var n = 2; used.Contains(id); n++)
        {
            id = slug + "-" + n;
        }

        return id;
    }
}

public interface ICustomRuleStore
{
    /// <summary>The saved rules. A missing file is an empty list; a damaged one is kept aside and reported in <paramref name="problem"/>.</summary>
    IReadOnlyList<CustomRule> Load(out string? problem);

    /// <returns>An error message when the rules could not be saved, otherwise <c>null</c>.</returns>
    string? Save(IReadOnlyList<CustomRule> rules);
}

/// <summary>Keeps the rules in one JSON file in the user's application data folder.</summary>
public sealed class FileCustomRuleStore : ICustomRuleStore
{
    private readonly string _path;

    public FileCustomRuleStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiagnosticStudio", "rules.json"))
    {
    }

    public FileCustomRuleStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public IReadOnlyList<CustomRule> Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(_path))
        {
            return Array.Empty<CustomRule>();
        }

        try
        {
            var text = File.ReadAllText(_path);
            if (CustomRuleJson.TryParse(text, out var rules, out var error))
            {
                return rules;
            }

            problem = KeepAside(error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The saved rules could not be read: " + ex.Message;
        }

        return Array.Empty<CustomRule>();
    }

    public string? Save(IReadOnlyList<CustomRule> rules)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, CustomRuleJson.Serialize(rules));
            File.Move(temp, _path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The rules could not be saved: " + ex.Message;
        }
    }

    private string KeepAside(string? error)
    {
        var aside = _path + ".bad";
        try
        {
            File.Copy(_path, aside, overwrite: true);
            return $"{error} The file was left as it was and copied to {aside}; saving a rule will replace it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return error ?? "The saved rules could not be read.";
        }
    }
}
