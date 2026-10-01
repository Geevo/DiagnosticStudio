using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DiagnosticStudio.Tests.App;

/// <summary>
/// The light and dark palettes are swapped at runtime; a brush missing from one would silently render as no
/// colour in that mode, so these keep them in step with each other and with what the views ask for.
/// </summary>
public sealed class PaletteTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppViewsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiagnosticStudio.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "DiagnosticStudio.App");
    }

    private static HashSet<string> Keys(string file) =>
        XDocument.Load(Path.Combine(AppViewsFolder(), "Views", file)).Root!
            .Elements()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Light_and_dark_palettes_define_the_same_brushes()
    {
        var light = Keys("Palette.Light.xaml");
        var dark = Keys("Palette.Dark.xaml");

        Assert.NotEmpty(light);
        Assert.Equal(light.OrderBy(k => k, StringComparer.Ordinal), dark.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_brush_the_views_reference_exists_in_the_palette()
    {
        var palette = Keys("Palette.Light.xaml");
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(AppViewsFolder(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:Dynamic|Static)Resource (\w+Brush)\}"))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        // FindResource-by-name usage from code.
        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(AppViewsFolder(), "Views", "HighlightedTextBlock.cs")), @"""(\w+Brush)"""))
        {
            used.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(palette));
    }

    [Fact]
    public void Views_do_not_hard_code_theme_colours()
    {
        var offenders = Directory.EnumerateFiles(AppViewsFolder(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !Path.GetFileName(f).StartsWith("Palette.", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "=\"#[0-9A-Fa-f]{6,8}\""))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
