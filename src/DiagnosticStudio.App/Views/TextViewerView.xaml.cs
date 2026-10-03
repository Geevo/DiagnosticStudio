using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.TextViewer;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: focus handling, scrolling and the clipboard. Viewer logic lives in <see cref="TextViewerViewModel"/>.</summary>
public partial class TextViewerView : UserControl
{
    // Selecting every line of a multi-million-line file would realise all of them.
    private const int SelectAllLimit = 50_000;

    private TextViewerViewModel? _viewModel;

    public TextViewerView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as TextViewerViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ScrollToLineRequested += OnScrollToLineRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The tab's content is recreated when switching tabs; put the selection back.
        if (_viewModel.CurrentLine > 0)
        {
            var line = _viewModel.CurrentLine;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SelectLine(line));
        }
    }

    private void OnScrollToLineRequested(object? sender, int line) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SelectLine(line));

    private void SelectLine(int line)
    {
        if (_viewModel is null || _viewModel.Lines.PositionOfLine(line) is not (>= 0 and var position))
        {
            return;
        }

        var item = _viewModel.Lines[position];
        LineList.SelectedItem = item;
        LineList.ScrollIntoView(item);
        CenterOn(position);
    }

    /// <summary>The line number shown at a place in the list (clamped to the list).</summary>
    private int LineAt(int position) =>
        _viewModel is { Lines.Count: > 0 } vm ? vm.Lines[Math.Clamp(position, 0, vm.Lines.Count - 1)].LineNumber : 1;

    // ScrollIntoView leaves the target at the very edge of the viewport; put it mid-view so surrounding context is visible.
    private void CenterOn(int index)
    {
        if (FindScrollViewer(LineList) is { ViewportHeight: > 0 } scroll)
        {
            scroll.ScrollToVerticalOffset(Math.Max(0, index - (scroll.ViewportHeight / 2)));
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && LineList.SelectedItem is LineViewModel line)
        {
            _viewModel.SetCurrentLineFromSelection(line.LineNumber);
        }
    }

    // Switching to text selection puts away the lines that were selected.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextViewerViewModel.FreeSelection) && _viewModel is { FreeSelection: true })
        {
            LineList.UnselectAll();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (_viewModel is { FreeSelection: true } free && LineList.IsKeyboardFocusWithin && HandleFreeSelectionKey(free, e, ctrl))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F when ctrl && !shift:
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
                break;
            case Key.L when ctrl:
                FilterBox.Focus();
                FilterBox.SelectAll();
                e.Handled = true;
                break;
            case Key.G when ctrl:
                GoToBox.Focus();
                GoToBox.SelectAll();
                e.Handled = true;
                break;
            case Key.F3:
                (shift ? _viewModel?.FindPreviousCommand : _viewModel?.FindNextCommand)?.Execute(null);
                e.Handled = true;
                break;
            case Key.A when ctrl && LineList.IsKeyboardFocusWithin && _viewModel?.Lines.Count > SelectAllLimit:
                e.Handled = true;
                break;
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _viewModel?.FindPreviousCommand : _viewModel?.FindNextCommand)
                    ?.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                LineList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel is not null)
            {
                _viewModel.FilterText = string.Empty;
            }

            LineList.Focus();
            e.Handled = true;
        }
    }

    private void OnGoToBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel?.GoToLineCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            LineList.Focus();
            e.Handled = true;
        }
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        // Can be queried while the XAML is still being loaded, before LineList is assigned.
        e.CanExecute = _viewModel is { FreeSelection: true }
            ? _viewModel.HasTextSelection
            : LineList is not null && LineList.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_viewModel.FreeSelection
                ? _viewModel.SelectedText(out _)
                : _viewModel.BuildCopyText(LineList.SelectedItems.OfType<LineViewModel>()));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard briefly locked by another process; the user can simply retry.
        }

        e.Handled = true;
    }
}
