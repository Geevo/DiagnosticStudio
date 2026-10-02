using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.TableViewer;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: builds the columns for this file, scrolls to a row, and the clipboard.</summary>
public partial class TableViewerView : UserControl
{
    private TableViewerViewModel? _viewModel;

    public TableViewerView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollRequested -= OnScrollRequested;
        }

        _viewModel = e.NewValue as TableViewerViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ScrollRequested += OnScrollRequested;
        RowList.View = BuildColumns(_viewModel);
    }

    // The columns are those of this file: a header row of a CSV, or the fixed ones of a CMTrace log.
    private static GridView BuildColumns(TableViewerViewModel viewModel)
    {
        var view = new GridView { AllowsColumnReorder = true };
        view.Columns.Add(new GridViewColumn
        {
            Header = "Line",
            Width = 70,
            DisplayMemberBinding = new Binding(nameof(TableRowViewModel.FirstLine)) { StringFormat = "N0" },
        });

        for (var i = 0; i < viewModel.Columns.Count; i++)
        {
            var column = viewModel.Columns[i];
            view.Columns.Add(new GridViewColumn
            {
                Header = column.Name,
                Width = column.Width,
                DisplayMemberBinding = new Binding($"{nameof(TableRowViewModel.Cells)}[{i}]"),
            });
        }

        return view;
    }

    private void OnScrollRequested(object? sender, int position)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_viewModel is { } vm && position < vm.Rows.Count)
            {
                RowList.ScrollIntoView(vm.Rows[position]);
            }
        });
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.ShowInRawSourceCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || _viewModel.SelectedTabIndex != TableViewerViewModel.TableTab)
        {
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FilterBox.IsKeyboardFocused)
        {
            _viewModel.ClearFiltersCommand.Execute(null);
            RowList.Focus();
            e.Handled = true;
        }
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = RowList is not null && RowList.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            CopyText(_viewModel.BuildCopyText(RowList.SelectedItems.OfType<TableRowViewModel>()));
        }

        e.Handled = true;
    }

    private void OnCopyDetail(object sender, RoutedEventArgs e) => CopyText(_viewModel?.DetailText);

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard briefly locked by another process; the user can retry.
        }
    }
}
