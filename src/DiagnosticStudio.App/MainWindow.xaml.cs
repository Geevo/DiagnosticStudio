using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiagnosticStudio.App.Services;
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
        _autoScroller = new AutoScroller(this);
    }

    private readonly AutoScroller _autoScroller;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    // Ctrl + mouse wheel zooms the interface, wherever the pointer is.
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ViewModel.Zoom.Wheel(e.Delta);
            e.Handled = true;
        }
    }

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

    // A single click on a file shows it in the preview tab. (Selecting with the keys previews after a short pause.)
    private void ExplorerItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item
            && ReferenceEquals(item, FindTreeViewItem(e.OriginalSource as DependencyObject))
            && item.DataContext is ExplorerNodeViewModel node
            && e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton)
        {
            ViewModel.Explorer.PreviewNodeCommand.Execute(node);
        }
    }

    // Double-clicking a tab keeps it.
    private void TabItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TabItem { DataContext: DocumentViewModel document })
        {
            ViewModel.Documents.PinCommand.Execute(document);
        }
    }

    // Right-click selects the item it lands on, as in File Explorer, so the menu acts on what the user sees chosen.
    private void ExplorerItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item && ReferenceEquals(item, FindTreeViewItem(e.OriginalSource as DependencyObject)))
        {
            item.IsSelected = true;
        }
    }

    private static TreeViewItem? FindTreeViewItem(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return source as TreeViewItem;
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
