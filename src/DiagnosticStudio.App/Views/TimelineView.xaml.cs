using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.Timeline;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: scrolling a selected row into view and activating rows.</summary>
public partial class TimelineView : UserControl
{
    private TimelineDocumentViewModel? _viewModel;

    public TimelineView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollRequested -= OnScrollRequested;
        }

        _viewModel = e.NewValue as TimelineDocumentViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollRequested += OnScrollRequested;
        }
    }

    private void OnScrollRequested(object? sender, int row)
    {
        // The list may have just been replaced; let the binding settle before scrolling.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_viewModel is { } vm && row < vm.Rows.Count)
            {
                EntryList.ScrollIntoView(vm.Rows[row]);
            }
        });
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel?.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    // The range boxes apply when they lose focus; Enter should apply too.
    private void OnTimeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
