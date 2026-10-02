using System.Runtime.InteropServices;
using System.Windows;

namespace DiagnosticStudio.App.Services;

public interface IClipboardService
{
    /// <summary>Puts text on the clipboard. Returns <c>false</c> when another program is holding the clipboard.</summary>
    bool SetText(string text);
}

public sealed class ClipboardService : IClipboardService
{
    private const int Attempts = 3;

    public bool SetText(string text)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                // Briefly locked by another process.
                Thread.Sleep(30);
            }
        }

        return false;
    }
}
