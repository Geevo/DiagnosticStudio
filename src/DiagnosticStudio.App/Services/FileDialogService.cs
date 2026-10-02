using Microsoft.Win32;

namespace DiagnosticStudio.App.Services;

public interface IFileDialogService
{
    string? PickBundleFile();

    string? PickBundleFolder();
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
        var dialog = new OpenFolderDialog { Title = "Open extracted diagnostic bundle folder" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
