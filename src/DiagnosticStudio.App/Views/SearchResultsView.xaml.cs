using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiagnosticStudio.App.ViewModels.Search;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: turns clicks and keys on result rows into view-model commands.</summary>
public partial class SearchResultsView : UserControl
{
    public SearchResultsView()
    {
        InitializeComponent();
    }

    private SearchResultsViewModel? ViewModel => DataContext as SearchResultsViewModel;

    // The innermost row handles the click; handling it stops enclosing rows from firing as the event bubbles.
    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: SearchHitViewModel hit })
        {
            ViewModel?.OpenHitCommand.Execute(hit);
            e.Handled = true;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: SearchArtifactNodeViewModel node })
        {
            ViewModel?.OpenArtifactCommand.Execute(node);
            e.Handled = true;
        }
    }

    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TreeViewItem item)
        {
            return;
        }

        switch (item.DataContext)
        {
            case SearchHitViewModel hit:
                ViewModel?.OpenHitCommand.Execute(hit);
                e.Handled = true;
                break;
            case SearchArtifactNodeViewModel node:
                ViewModel?.OpenArtifactCommand.Execute(node);
                e.Handled = true;
                break;
        }
    }
}
