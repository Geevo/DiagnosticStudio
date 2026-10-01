using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.RegistryViewer;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: focus, scrolling selections into view and the clipboard.</summary>
public partial class RegistryViewerView : UserControl
{
    private RegistryViewerViewModel? _viewModel;

    public RegistryViewerView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ValueRevealRequested -= OnValueRevealRequested;
            _viewModel.KeyRevealRequested -= OnKeyRevealRequested;
        }

        _viewModel = e.NewValue as RegistryViewerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ValueRevealRequested += OnValueRevealRequested;
            _viewModel.KeyRevealRequested += OnKeyRevealRequested;

            // The tab's content is recreated on tab switches; restore the scroll position of the selection.
            if (_viewModel.SelectedKey?.Key is { } selected)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealInTree(selected));
            }

            if (_viewModel.SelectedValue is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RevealSelectedValue);
            }
        }
    }

    // The tree is detached and re-attached whenever the user switches tabs, and it forgets its scroll position. Each
    // time it loads, bring the selected key back into view.
    private void OnTreeLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedKey?.Key is { } selected)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealInTree(selected));
        }
    }

    private void OnValueRevealRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RevealSelectedValue);

    private void RevealSelectedValue()
    {
        if (_viewModel?.SelectedValue is { } value)
        {
            ValueList.ScrollIntoView(value);
        }
    }

    private void OnKeyRevealRequested(object? sender, RegistryKey key) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealInTree(key));

    /// <summary>
    /// Scrolls the (virtualised) key tree to <paramref name="key"/>. Containers of off-screen items do not exist, so
    /// each level is scrolled into view first, which makes WPF generate the container for the next level down.
    /// </summary>
    private void RevealInTree(RegistryKey key)
    {
        var chain = key.Ancestors().Reverse().Append(key).ToList();
        ItemsControl parent = KeyTree;
        TreeViewItem? last = null;

        foreach (var step in chain)
        {
            var index = IndexOfKey(parent, step);
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

    private static int IndexOfKey(ItemsControl parent, RegistryKey key)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.Items[i] is RegistryKeyNodeViewModel node && ReferenceEquals(node.Key, key))
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
        if (_viewModel is null || _viewModel.SelectedTabIndex != RegistryViewerViewModel.RegistryTab)
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
            KeyTree.Focus();
            e.Handled = true;
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e) => CopyText(_viewModel?.SelectedKeyPath);

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
