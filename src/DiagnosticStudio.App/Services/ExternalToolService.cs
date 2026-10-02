using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Hands a file to another program so the investigator can read it there. Only editors and File Explorer are
/// offered, and always with the file as an argument; a file from a bundle is never launched itself, because that
/// could run it.
/// </summary>
public interface IExternalToolService
{
    /// <summary>Full path of Notepad++ when it is installed; otherwise <c>null</c>.</summary>
    string? NotepadPlusPlusPath { get; }

    /// <summary>Opens the file in Notepad. Throws <see cref="ExternalToolException"/> when it cannot be started.</summary>
    void OpenInNotepad(string path);

    void OpenInNotepadPlusPlus(string path);

    /// <summary>Opens File Explorer with the file selected.</summary>
    void ShowInExplorer(string path);
}

public sealed class ExternalToolException : Exception
{
    public ExternalToolException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class ExternalToolService : IExternalToolService
{
    private readonly Lazy<string?> _notepadPlusPlus = new(() => NotepadPlusPlusLocator.Find());

    public string? NotepadPlusPlusPath => _notepadPlusPlus.Value;

    public void OpenInNotepad(string path) =>
        Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Quote(path), path);

    public void OpenInNotepadPlusPlus(string path)
    {
        var exe = NotepadPlusPlusPath ?? throw new ExternalToolException("Notepad++ is not installed.");
        Start(exe, Quote(path), path);
    }

    public void ShowInExplorer(string path) =>
        Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select," + Quote(path), path);

    private static string Quote(string path) => "\"" + path + "\"";

    private static void Start(string program, string arguments, string path)
    {
        if (!File.Exists(path))
        {
            throw new ExternalToolException("The file is no longer there: " + path);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new ExternalToolException("Could not start " + Path.GetFileName(program) + ": " + ex.Message, ex);
        }
    }
}

/// <summary>Finds Notepad++ without running anything: the usual install folders, then its registry entry.</summary>
internal static class NotepadPlusPlusLocator
{
    public static string? Find(
        Func<string, bool>? fileExists = null,
        Func<string, string?>? environment = null,
        Func<IEnumerable<string>>? registryFolders = null)
    {
        fileExists ??= File.Exists;
        environment ??= Environment.GetEnvironmentVariable;
        registryFolders ??= InstallFoldersFromRegistry;

        var folders = new List<string>();
        foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            if (environment(variable) is { Length: > 0 } root)
            {
                folders.Add(Path.Combine(root, "Notepad++"));
            }
        }

        if (environment("LocalAppData") is { Length: > 0 } local)
        {
            folders.Add(Path.Combine(local, "Programs", "Notepad++"));
        }

        folders.AddRange(registryFolders());

        foreach (var folder in folders)
        {
            var exe = Path.Combine(folder, "notepad++.exe");
            if (fileExists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    private static IEnumerable<string> InstallFoldersFromRegistry()
    {
        var found = new List<string>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Notepad++");
                if (key?.GetValue(null) is string { Length: > 0 } folder)
                {
                    found.Add(folder);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable: treat as not installed there.
            }
        }

        return found;
    }
}
