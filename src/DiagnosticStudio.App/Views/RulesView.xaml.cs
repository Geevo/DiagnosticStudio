using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DiagnosticStudio.App.ViewModels.CustomRules;

namespace DiagnosticStudio.App.Views;

public partial class RulesView : UserControl
{
    private static readonly GridLength HelpWidth = new(370);

    private GridLength _helpWidth = HelpWidth;

    public RulesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is RulesDocumentViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }

            if (e.NewValue is RulesDocumentViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                ShowHelp(vm.ShowHelp);
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RulesDocumentViewModel.ShowHelp) && sender is RulesDocumentViewModel vm)
        {
            ShowHelp(vm.ShowHelp);
        }
    }

    // The help column is given no width at all when it is off, so the editor can use the room.
    private void ShowHelp(bool show)
    {
        if (!show && HelpColumn.Width.Value > 0)
        {
            _helpWidth = HelpColumn.Width;
        }

        HelpGapColumn.Width = show ? GridLength.Auto : new GridLength(0);
        HelpColumn.MinWidth = show ? 240 : 0;
        HelpColumn.Width = show ? _helpWidth : new GridLength(0);
    }
}
