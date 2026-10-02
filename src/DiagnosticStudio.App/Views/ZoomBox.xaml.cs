using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.App.Views;

public partial class ZoomBox : UserControl
{
    public ZoomBox()
    {
        InitializeComponent();

        // Every box shows the one document zoom of the window it is in.
        SetBinding(DataContextProperty, new Binding("DataContext.ContentZoom")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
        });
    }

    private ZoomViewModel? Zoom => DataContext as ZoomViewModel;

    // A level picked from the list applies at once.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Zoom is { } zoom && e.AddedItems.Count == 1 && e.AddedItems[0] is string picked && picked != zoom.Entry)
        {
            zoom.Entry = picked;
        }
    }

    // A typed level applies on Enter or when the box is left, whichever comes first.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Zoom is { } zoom)
        {
            zoom.Entry = Box.Text;
            Box.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Zoom is { } current)
        {
            Box.Text = current.Entry;
            e.Handled = true;
        }
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Zoom is { } zoom && !Box.IsKeyboardFocusWithin && Box.Text != zoom.Entry)
        {
            zoom.Entry = Box.Text;
        }
    }
}
