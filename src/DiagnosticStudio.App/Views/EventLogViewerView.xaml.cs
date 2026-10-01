using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.EventLogViewer;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: focus, scrolling rows into view and the clipboard.</summary>
public partial class EventLogViewerView : UserControl
{
    private EventLogViewerViewModel? _viewModel;

    public EventLogViewerView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToRowRequested -= OnScrollToRowRequested;
        }

        _viewModel = e.NewValue as EventLogViewerViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ScrollToRowRequested += OnScrollToRowRequested;

        // The tab's content is recreated when switching tabs; bring the selection back into view.
        if (_viewModel.SelectedEvent is { } selected && _viewModel.Events.RowOfEvent(selected.EventIndex) is var row and >= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToRow(row));
        }
    }

    private void OnScrollToRowRequested(object? sender, int row) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToRow(row));

    private void ScrollToRow(int row)
    {
        if (_viewModel is null || row < 0 || row >= _viewModel.Events.Count)
        {
            return;
        }

        EventList.ScrollIntoView(_viewModel.Events[row]);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EventList.Focus();
            e.Handled = true;
        }
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        // Can be queried while the XAML is still loading.
        e.CanExecute = _viewModel?.SelectedDetail is not null;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        CopyXml();
        e.Handled = true;
    }

    private void OnCopyXml(object sender, RoutedEventArgs e) => CopyXml();

    private void CopyXml()
    {
        if (_viewModel?.SelectedDetail?.Xml is not { Length: > 0 } xml)
        {
            return;
        }

        try
        {
            Clipboard.SetText(xml);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard briefly locked by another process; the user can retry.
        }
    }
}
