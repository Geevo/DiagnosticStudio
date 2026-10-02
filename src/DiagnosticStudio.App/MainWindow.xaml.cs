using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.Views;

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

    // Ctrl + mouse wheel zooms the interface, except over a document, which has a zoom of its own.
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !ScaledContent.Contains(e.OriginalSource as DependencyObject))
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

    // Moving through the tree with the keys brings the selected item into view, and a long name makes the tree
    // scroll sideways to show all of it. Only the vertical part is wanted, so the request is repeated with a
    // sliver of the item's left edge, which keeps it in line without moving the view across. (The tree item
    // aims the request at its own header, so that is the target to look for.) Expanding a node is the exception:
    // then its first child is brought fully into view, sideways as well.
    private bool _bringingIntoView;
    private bool _allowSideways;
    private bool _expandedByUser;

    private void ExplorerItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_bringingIntoView
            || _allowSideways
            || sender is not TreeViewItem item
            || e.TargetObject is not FrameworkElement target
            || !ReferenceEquals(FindTreeViewItem(target), item))
        {
            return;
        }

        e.Handled = true;
        _bringingIntoView = true;
        try
        {
            var rect = e.TargetRect.IsEmpty ? new Rect(target.RenderSize) : e.TargetRect;
            target.BringIntoView(new Rect(rect.X, rect.Y, Math.Min(rect.Width, 1), rect.Height));
        }
        finally
        {
            _bringingIntoView = false;
        }
    }

    // Only an expansion the user asked for (the arrow keys, + and *, or a click on the arrow) moves the view.
    // The Expand all button and the filter expand things too, and those must leave it alone.
    private void ExplorerItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Right or Key.Add or Key.Multiply)
        {
            MarkExpandedByUser();
        }
    }

    private void ExplorerItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is not null)
        {
            MarkExpandedByUser();
        }
    }

    private void MarkExpandedByUser()
    {
        _expandedByUser = true;
        Dispatcher.BeginInvoke(() => _expandedByUser = false, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ExplorerItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (!_expandedByUser || sender is not TreeViewItem item || !ReferenceEquals(e.OriginalSource, item) || item.Items.Count == 0)
        {
            return;
        }

        // Once the children have been laid out.
        Dispatcher.BeginInvoke(
            () =>
            {
                if (item.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
                {
                    _allowSideways = true;
                    try
                    {
                        first.BringIntoView();
                    }
                    finally
                    {
                        _allowSideways = false;
                    }
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return source as T;
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
