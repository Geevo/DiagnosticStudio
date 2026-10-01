using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.RegistryViewer;

/// <summary>
/// Tree node for a registry key. Child nodes are created only when the node is first expanded, so a
/// hive with a very large number of keys never materialises them all as UI objects.
/// </summary>
public sealed partial class RegistryKeyNodeViewModel : ObservableObject
{
    private readonly Action<RegistryKeyNodeViewModel>? _onSelected;
    private bool _childrenLoaded;

    public RegistryKeyNodeViewModel(RegistryKey? key, Action<RegistryKeyNodeViewModel>? onSelected)
    {
        Key = key;
        _onSelected = onSelected;
        Children = new ObservableCollection<RegistryKeyNodeViewModel>();

        // Placeholder child so the tree shows an expander until the real children are loaded.
        if (key is { Children.Count: > 0 })
        {
            Children.Add(new RegistryKeyNodeViewModel(null, null));
        }
        else
        {
            _childrenLoaded = true;
        }
    }

    /// <summary><c>null</c> for the expander placeholder.</summary>
    public RegistryKey? Key { get; }

    public bool IsPlaceholder => Key is null;

    public string DisplayName => Key is null ? "…" : Key.IsDeleted ? Key.Name + " (deleted)" : Key.Name;

    public bool IsDeleted => Key?.IsDeleted == true;

    public string? ToolTip => Key?.FullPath;

    public ObservableCollection<RegistryKeyNodeViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildren();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _onSelected?.Invoke(this);
        }
    }

    public void EnsureChildren()
    {
        if (_childrenLoaded || Key is null)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in Key.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            Children.Add(new RegistryKeyNodeViewModel(child, _onSelected));
        }
    }

    public RegistryKeyNodeViewModel? FindChild(RegistryKey key)
    {
        EnsureChildren();
        return Children.FirstOrDefault(c => ReferenceEquals(c.Key, key));
    }
}
