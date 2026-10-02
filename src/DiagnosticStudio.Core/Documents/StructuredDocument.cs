using System.Globalization;
using System.Text;

namespace DiagnosticStudio.Core.Documents;

public enum StructuredFormat
{
    Xml,
    Json,
}

public enum StructuredNodeKind
{
    /// <summary>Synthetic container for several top-level items (XML always; JSON Lines).</summary>
    Document,
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
    Element,
    Attribute,
    Text,
    Comment,
}

/// <summary>
/// One node of a parsed XML or JSON file. Nodes are built once by a parser and not changed afterwards.
/// </summary>
public sealed class StructuredNode
{
    /// <summary>Longest value kept in full; longer values are cut and flagged. The raw source always holds the rest.</summary>
    public const int MaxStoredValueChars = 16384;

    private readonly List<StructuredNode> _children = new();

    public StructuredNode(StructuredNodeKind kind, string? name, string? value, int ordinal, int line, StructuredNode? parent)
    {
        Kind = kind;
        Name = name;
        Ordinal = ordinal;
        Line = line;
        Parent = parent;
        if (value is not null)
        {
            ValueLength = value.Length;
            if (value.Length > MaxStoredValueChars)
            {
                Value = value[..MaxStoredValueChars];
                IsValueTruncated = true;
            }
            else
            {
                Value = value;
            }
        }
    }

    public StructuredNodeKind Kind { get; }

    /// <summary>JSON member name, or XML element/attribute name. <c>null</c> for array items, text and comments.</summary>
    public string? Name { get; }

    /// <summary>Scalar text: a JSON scalar as written, an attribute value, text, or a comment. <c>null</c> for containers.</summary>
    public string? Value { get; }

    public int ValueLength { get; }
    public bool IsValueTruncated { get; }

    /// <summary>
    /// JSON: zero-based position among the parent's children. XML: one-based position among siblings of the same
    /// kind and name (elements by name, text, comments); attributes are always 1.
    /// </summary>
    public int Ordinal { get; }

    /// <summary>One-based line in the source where the node starts.</summary>
    public int Line { get; }

    public StructuredNode? Parent { get; private set; }

    public IReadOnlyList<StructuredNode> Children => _children;

    public bool IsContainer => Kind is StructuredNodeKind.Document or StructuredNodeKind.Object or StructuredNodeKind.Array
        or StructuredNodeKind.Element;

    public StructuredNode AddChild(StructuredNodeKind kind, string? name, string? value, int ordinal, int line)
    {
        var child = new StructuredNode(kind, name, value, ordinal, line, this);
        _children.Add(child);
        return child;
    }

    /// <summary>For parsers: detaches and returns the only child of a synthetic document node so it can serve as the root.</summary>
    public StructuredNode PromoteOnlyChild()
    {
        if (_children.Count != 1)
        {
            throw new InvalidOperationException("The node does not have exactly one child.");
        }

        var child = _children[0];
        child.Parent = null;
        _children.Clear();
        return child;
    }
}

/// <summary>
/// Stable text addresses for nodes, used in <see cref="Navigation.DiagnosticLocation"/>.
/// JSON uses JSON Pointer (RFC 6901): <c>/items/0/name</c>, with <c>~0</c> and <c>~1</c> escapes; the root is the empty
/// string. XML uses positional XPath: <c>/Config[1]/Item[2]/@id</c>, <c>/Config[1]/Item[2]/text()[1]</c>,
/// <c>comment()[n]</c>; the document itself is <c>/</c>.
/// </summary>
public static class StructuredPaths
{
    public static string PathOf(StructuredFormat format, StructuredNode node) =>
        format == StructuredFormat.Json ? JsonPointer(node) : XmlPath(node);

    public static StructuredNode? Resolve(StructuredFormat format, StructuredNode root, string path) =>
        format == StructuredFormat.Json ? ResolveJson(root, path) : ResolveXml(root, path);

    // ---- JSON Pointer ----

    private static string JsonPointer(StructuredNode node)
    {
        var segments = new Stack<string>();
        for (var current = node; current.Parent is { } parent; current = parent)
        {
            segments.Push(parent.Kind == StructuredNodeKind.Object
                ? (current.Name ?? string.Empty).Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)
                : current.Ordinal.ToString(CultureInfo.InvariantCulture));
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append('/').Append(segment);
        }

        return sb.ToString();
    }

    private static StructuredNode? ResolveJson(StructuredNode root, string path)
    {
        if (path.Length == 0)
        {
            return root;
        }

        if (path[0] != '/')
        {
            return null;
        }

        var current = root;
        foreach (var raw in path[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.Kind == StructuredNodeKind.Object)
            {
                current = current.Children.FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.Ordinal))!;
            }
            else if (current.Kind is StructuredNodeKind.Array or StructuredNodeKind.Document
                     && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                     && index < current.Children.Count)
            {
                current = current.Children[index];
            }
            else
            {
                return null;
            }

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    // ---- XML ----

    private static string XmlPath(StructuredNode node)
    {
        var segments = new Stack<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            segments.Push(current.Kind switch
            {
                StructuredNodeKind.Attribute => "@" + current.Name,
                StructuredNodeKind.Text => "text()[" + current.Ordinal.ToString(CultureInfo.InvariantCulture) + "]",
                StructuredNodeKind.Comment => "comment()[" + current.Ordinal.ToString(CultureInfo.InvariantCulture) + "]",
                _ => current.Name + "[" + current.Ordinal.ToString(CultureInfo.InvariantCulture) + "]",
            });
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static StructuredNode? ResolveXml(StructuredNode root, string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            return null;
        }

        var current = root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            StructuredNodeKind kind;
            string? name = null;
            var ordinal = 1;

            if (segment[0] == '@')
            {
                kind = StructuredNodeKind.Attribute;
                name = segment[1..];
            }
            else
            {
                var open = segment.IndexOf('[', StringComparison.Ordinal);
                var test = open < 0 ? segment : segment[..open];
                if (open >= 0)
                {
                    if (!segment.EndsWith(']') || !int.TryParse(segment[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out ordinal))
                    {
                        return null;
                    }
                }

                switch (test)
                {
                    case "text()":
                        kind = StructuredNodeKind.Text;
                        break;
                    case "comment()":
                        kind = StructuredNodeKind.Comment;
                        break;
                    default:
                        kind = StructuredNodeKind.Element;
                        name = test;
                        break;
                }
            }

            var next = current.Children.FirstOrDefault(c =>
                c.Kind == kind
                && (kind is StructuredNodeKind.Text or StructuredNodeKind.Comment || string.Equals(c.Name, name, StringComparison.Ordinal))
                && (kind == StructuredNodeKind.Attribute || c.Ordinal == ordinal));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}

/// <summary>An XML or JSON file as a tree, together with its raw text. Both are always available.</summary>
public sealed record StructuredDocument : DiagnosticDocument
{
    public required StructuredFormat Format { get; init; }

    /// <summary>
    /// JSON: the single top-level value, or a <see cref="StructuredNodeKind.Document"/> holding several (JSON Lines).
    /// XML: always a <see cref="StructuredNodeKind.Document"/> whose children are the top-level elements.
    /// </summary>
    public required StructuredNode Root { get; init; }

    /// <summary>The complete file as text, for the raw view and for line-based search.</summary>
    public required ITextLineSource RawSource { get; init; }

    public int NodeCount { get; init; }

    /// <summary>
    /// Set when the text stops being well-formed (a file cut off while it was written, a stray character): the tree holds
    /// what came before that point, and the raw source has everything. <c>null</c> for a file that was read to the end.
    /// </summary>
    public string? ReadProblem { get; init; }

    /// <summary>One-based line where reading stopped, or 0 when unknown.</summary>
    public int ReadProblemLine { get; init; }

    public string PathOf(StructuredNode node) => StructuredPaths.PathOf(Format, node);

    public StructuredNode? FindNode(string path) => StructuredPaths.Resolve(Format, Root, path);

    /// <summary>
    /// The node a source line belongs to: the last node, in document order, that starts on or before
    /// <paramref name="line"/>. <c>null</c> when the line is before everything.
    /// </summary>
    public StructuredNode? NodeAtLine(int line)
    {
        var current = Root;
        if (Root.Line > line && Root.Kind != StructuredNodeKind.Document)
        {
            return null;
        }

        StructuredNode? best = Root.Kind == StructuredNodeKind.Document ? null : Root;
        while (true)
        {
            var children = current.Children;
            var low = 0;
            var high = children.Count - 1;
            var found = -1;

            // Children are in document order, so their start lines never decrease.
            while (low <= high)
            {
                var mid = (low + high) >> 1;
                if (children[mid].Line <= line)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (found < 0)
            {
                return best;
            }

            current = children[found];
            best = current;
        }
    }
}
