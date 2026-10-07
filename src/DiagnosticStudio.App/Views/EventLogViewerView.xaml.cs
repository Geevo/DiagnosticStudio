using System.ComponentModel;
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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as EventLogViewerViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ScrollToRowRequested += OnScrollToRowRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateColumnHeaders();

        // The tab's content is recreated when switching tabs; bring the selection back into view.
        if (_viewModel.SelectedEvent is { } selected && _viewModel.Events.RowOfEvent(selected.EventIndex) is var row and >= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToRow(row));
        }
    }

    private IEnumerable<(GridViewColumn Column, string Title, EventSortColumn Sort)> SortableColumns()
    {
        yield return (TimeColumn, "Time (UTC)", EventSortColumn.Time);
        yield return (LevelColumn, "Level", EventSortColumn.Level);
        yield return (ProviderColumn, "Provider", EventSortColumn.Provider);
        yield return (EventIdColumn, "Event ID", EventSortColumn.EventId);
        yield return (RecordColumn, "Record", EventSortColumn.Record);
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || e.OriginalSource is not GridViewColumnHeader { Column: { } column })
        {
            return;
        }

        foreach (var (candidate, _, sort) in SortableColumns())
        {
            if (candidate == column)
            {
                _viewModel.SortBy(sort);
                return;
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EventLogViewerViewModel.SortColumn) or nameof(EventLogViewerViewModel.SortDescending))
        {
            UpdateColumnHeaders();
        }
    }

    /// <summary>Marks the sort column's header with an arrow: up for ascending, down for descending.</summary>
    private void UpdateColumnHeaders()
    {
        if (_viewModel is null)
        {
            return;
        }

        foreach (var (column, title, sort) in SortableColumns())
        {
            column.Header = sort != _viewModel.SortColumn ? title : title + (_viewModel.SortDescending ? " \u25BC" : " \u25B2");
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
