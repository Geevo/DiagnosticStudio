using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: the logo, closing, and opening the repository address in the browser.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Logo.Source = LargestFrame(new Uri("pack://application:,,,/Assets/app.ico"));
    }

    // An icon file holds several sizes; the default pick is the smallest, which looks soft at 64 pixels.
    private static BitmapSource? LargestFrame(Uri uri)
    {
        try
        {
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Only the web address of the project; the link text comes from the build, never from a file being investigated.
        if (e.Uri is { Scheme: "https" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No default browser is set up; the address is still shown and can be copied.
            }
        }

        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
