using System.Windows.Controls;
using System.Windows.Input;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.App.Views;

/// <summary>View-only behaviour: turns clicks and keys on findings and evidence into view-model commands.</summary>
public partial class ProblemsView : UserControl
{
    public ProblemsView()
    {
        InitializeComponent();
    }

    private ProblemsViewModel? ViewModel => DataContext as ProblemsViewModel;

    // The innermost row handles the click; handling it stops enclosing rows from firing as the event bubbles.
    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (Activate(sender))
        {
            e.Handled = true;
        }
    }

    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Activate(sender))
        {
            e.Handled = true;
        }
    }

    private bool Activate(object sender)
    {
        switch ((sender as TreeViewItem)?.DataContext)
        {
            case EvidenceViewModel evidence:
                ViewModel?.OpenEvidenceCommand.Execute(evidence);
                return true;
            case FindingViewModel finding:
                ViewModel?.OpenFindingCommand.Execute(finding);
                return true;
            default:
                return false;
        }
    }
}
