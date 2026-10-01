using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.StructuredViewer;

/// <summary>
/// Tree node for an XML or JSON node. Children are created only when the node is first expanded, so an array with
/// hundreds of thousands of items never becomes that many UI objects until someone opens it.
/// </summary>
public sealed partial class StructuredNodeViewModel : ObservableObject
{
    private const int MaxSummaryChars = 200;

    private readonly Action<StructuredNodeViewModel>? _onSelected;
    private bool _childrenLoaded;

    public StructuredNodeViewModel(StructuredNode? node, StructuredFormat format, Action<StructuredNodeViewModel>? onSelected)
    {
        Node = node;
        Format = format;
        _onSelected = onSelected;
        Children = new ObservableCollection<StructuredNodeViewModel>();

        if (node is not null)
        {
            DisplayName = NameOf(node);
            Summary = SummaryOf(node);
            Glyph = GlyphOf(node.Kind);
        }
        else
        {
            DisplayName = "…";
        }

        // Placeholder child so the tree shows an expander until the real children are loaded.
        if (node is not null && HasVisibleChildren(node))
        {
            Children.Add(new StructuredNodeViewModel(null, format, null));
        }
        else
        {
            _childrenLoaded = true;
        }
    }

    /// <summary><c>null</c> for the expander placeholder.</summary>
    public StructuredNode? Node { get; }

    public StructuredFormat Format { get; }
    public bool IsPlaceholder => Node is null;
    public string DisplayName { get; }
    public string Summary { get; } = string.Empty;
    public string Glyph { get; } = string.Empty;
    public ObservableCollection<StructuredNodeViewModel> Children { get; }

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
        if (_childrenLoaded || Node is null)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in Node.Children)
        {
            Children.Add(new StructuredNodeViewModel(child, Format, _onSelected));
        }
    }

    public StructuredNodeViewModel? FindChild(StructuredNode node)
    {
        EnsureChildren();
        return Children.FirstOrDefault(c => ReferenceEquals(c.Node, node));
    }

    /// <summary>An element whose only content is text shows that text on its own row instead of a child node.</summary>
    internal static bool IsInlineText(StructuredNode node) =>
        node.Kind == StructuredNodeKind.Element
        && node.Children.Count == 1
        && node.Children[0].Kind == StructuredNodeKind.Text;

    private static bool HasVisibleChildren(StructuredNode node) => node.Children.Count > 0 && !IsInlineText(node);

    private static string NameOf(StructuredNode node)
    {
        switch (node.Kind)
        {
            case StructuredNodeKind.Attribute:
                return "@" + node.Name;
            case StructuredNodeKind.Text:
                return "(text)";
            case StructuredNodeKind.Comment:
                return "(comment)";
            case StructuredNodeKind.Element:
                return node.Name ?? string.Empty;
        }

        // JSON
        if (node.Parent is null)
        {
            return "(root)";
        }

        return node.Parent.Kind == StructuredNodeKind.Object
            ? node.Name ?? string.Empty
            : "[" + node.Ordinal.ToString(CultureInfo.InvariantCulture) + "]";
    }

    private static string SummaryOf(StructuredNode node)
    {
        var count = node.Children.Count.ToString("N0", CultureInfo.CurrentCulture);
        switch (node.Kind)
        {
            case StructuredNodeKind.Object:
                return "{" + count + "}";
            case StructuredNodeKind.Array:
                return "[" + count + "]";
            case StructuredNodeKind.Document:
                return count + " items";
            case StructuredNodeKind.String:
                return "\"" + OneLine(node.Value) + "\"";
            case StructuredNodeKind.Number:
            case StructuredNodeKind.Boolean:
            case StructuredNodeKind.Null:
                return node.Value ?? string.Empty;
            case StructuredNodeKind.Attribute:
                return "\"" + OneLine(node.Value) + "\"";
            case StructuredNodeKind.Text:
            case StructuredNodeKind.Comment:
                return OneLine(node.Value);
            case StructuredNodeKind.Element:
                return IsInlineText(node) ? OneLine(node.Children[0].Value) : string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value.Replace("\r\n", "↵", StringComparison.Ordinal).Replace('\n', '↵').Replace('\r', '↵').Replace('\t', ' ');
        return text.Length > MaxSummaryChars ? text[..MaxSummaryChars] + "…" : text;
    }

    private static string GlyphOf(StructuredNodeKind kind) => kind switch
    {
        StructuredNodeKind.Object => "{}",
        StructuredNodeKind.Array => "[]",
        StructuredNodeKind.String => "ab",
        StructuredNodeKind.Number => "12",
        StructuredNodeKind.Boolean => "tf",
        StructuredNodeKind.Null => "∅",
        StructuredNodeKind.Element => "<>",
        StructuredNodeKind.Attribute => "@",
        StructuredNodeKind.Text => "T",
        StructuredNodeKind.Comment => "//",
        _ => "·",
    };
}
