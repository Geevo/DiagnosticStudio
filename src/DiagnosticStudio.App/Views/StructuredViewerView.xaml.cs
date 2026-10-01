using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.StructuredViewer;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: focus, scrolling selections into view and the clipboard.</summary>
public partial class StructuredViewerView : UserControl
{
    private StructuredViewerViewModel? _viewModel;

    public StructuredViewerView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.NodeRevealRequested -= OnNodeRevealRequested;
        }

        _viewModel = e.NewValue as StructuredViewerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NodeRevealRequested += OnNodeRevealRequested;

        }
    }

    // The tree is detached and re-attached whenever the user switches tabs, and it forgets its scroll position. Each
    // time it loads, bring the selection (which may have been moved while the Raw tab was showing) back into view.
    private void OnTreeLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedNode?.Node is { } selected)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealInTree(selected));
        }
    }

    private void OnNodeRevealRequested(object? sender, StructuredNode node) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealInTree(node));

    /// <summary>
    /// Scrolls the (virtualised) tree to <paramref name="node"/>. Containers of off-screen items do not exist, so each
    /// level is scrolled into view first, which makes WPF generate the container for the next level down.
    /// </summary>
    private void RevealInTree(StructuredNode node)
    {
        var chain = new List<StructuredNode>();
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current.Kind != StructuredNodeKind.Document)
            {
                chain.Add(current);
            }
        }

        chain.Reverse();

        ItemsControl parent = NodeTree;
        TreeViewItem? last = null;
        foreach (var step in chain)
        {
            var index = IndexOfNode(parent, step);
            if (index < 0)
            {
                return;
            }

            if (FindItemsHost(parent) is VirtualizingPanel panel)
            {
                panel.BringIndexIntoViewPublic(index);
            }

            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem container)
            {
                return;
            }

            container.ApplyTemplate();
            container.UpdateLayout();
            last = container;
            parent = container;
        }

        last?.BringIntoView();
    }

    private static int IndexOfNode(ItemsControl parent, StructuredNode node)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.Items[i] is StructuredNodeViewModel vm && ReferenceEquals(vm.Node, node))
            {
                return i;
            }
        }

        return -1;
    }

    private static Panel? FindItemsHost(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Panel { IsItemsHost: true } panel)
            {
                return panel;
            }

            if (child is not TreeViewItem && FindItemsHost(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The raw source tab hosts its own viewer with its own Ctrl+F.
        if (_viewModel is null || _viewModel.SelectedTabIndex != StructuredViewerViewModel.StructureTab)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.Key == Key.F && ctrl && !shift)
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            (shift ? _viewModel.FindPreviousCommand : _viewModel.FindNextCommand).Execute(null);
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _viewModel.FindPreviousCommand : _viewModel.FindNextCommand)
                .Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NodeTree.Focus();
            e.Handled = true;
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e) => CopyText(_viewModel?.SelectedPath);

    private void OnCopyValue(object sender, RoutedEventArgs e) => CopyText(_viewModel?.DetailText);

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
