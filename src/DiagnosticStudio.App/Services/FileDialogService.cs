using Microsoft.Win32;

namespace DiagnosticStudio.App.Services;

public interface IFileDialogService
{
    string? PickBundleFile();

    string? PickBundleFolder();

    /// <summary>Asks for a JSON file to read, or <c>null</c> if cancelled.</summary>
    string? PickJsonToOpen(string title);

    /// <summary>Asks where to save a JSON file, or <c>null</c> if cancelled.</summary>
    string? PickJsonToSave(string title, string suggestedName);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? PickBundleFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open diagnostic archive",
            Filter = "Archives (*.zip;*.cab)|*.zip;*.cab|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickBundleFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Open extracted diagnostics folder" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickJsonToOpen(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickJsonToSave(string title, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
