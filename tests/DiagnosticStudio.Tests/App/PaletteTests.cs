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

        // Menus.xaml is the Fluent theme's own menu templates, which use that theme's brushes, not the palette.
        foreach (var file in Directory.EnumerateFiles(AppViewsFolder(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(f) != "Menus.xaml")
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
}
