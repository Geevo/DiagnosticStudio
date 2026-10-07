using System.Reflection;

namespace DiagnosticStudio.App.Services;

/// <summary>What the About dialog says about this build. Version and repository come from the build, not from text kept here.</summary>
public sealed record AppInfo(string Name, string Version, string RepositoryUrl)
{
    public static AppInfo FromAssembly(Assembly assembly)
    {
        // The informational version carries the source revision after a '+'; the dialog shows the release number only.
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? string.Empty;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        var repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? string.Empty;

        return new AppInfo("Diagnostic Studio", version, repository);
    }
}
