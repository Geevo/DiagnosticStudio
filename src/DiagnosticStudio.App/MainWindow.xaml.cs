using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.App;

/// <summary>
/// View-only behaviour: drag/drop and tree activation are forwarded to view-model commands.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SearchFocusRequested += (_, _) =>
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => Close()));
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            ViewModel.OpenPathCommand.Execute(paths[0]);
        }

        e.Handled = true;
    }

    private void ExplorerItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Nested TreeViewItems all raise this; only act for the item that was actually clicked.
        if (sender is TreeViewItem { IsSelected: true, DataContext: ExplorerNodeViewModel node })
        {
            ViewModel.Explorer.OpenNodeCommand.Execute(node);
            e.Handled = true;
        }
    }

    private void ExplorerItem_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TreeViewItem { IsSelected: true, DataContext: ExplorerNodeViewModel node })
        {
            ViewModel.Explorer.OpenNodeCommand.Execute(node);
            e.Handled = true;
        }
    }
}
