using System.Text;
using System.Text.Json;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers.Structured;

/// <summary>
/// Builds a <see cref="StructuredNode"/> tree from JSON. It accepts what real diagnostic tools write: comments,
/// trailing commas, a byte-order mark, UTF-16, and several top-level values in a row (JSON Lines). Numbers keep their
/// original text, so nothing is rounded. Depth and node count are capped and the tree is built without recursion.
/// </summary>
public static class JsonStructureReader
{
    public const int DefaultMaxDepth = 512;
    public const int DefaultMaxNodes = 2_000_000;

    /// <returns>
    /// The single top-level value, or a document node holding several; and the number of nodes created.
    /// </returns>
    /// <exception cref="InvalidDataException">The content is not valid JSON, or exceeds the limits.</exception>
    public static (StructuredNode Root, int NodeCount) Read(
        byte[] content,
        CancellationToken cancellationToken = default,
        int maxDepth = DefaultMaxDepth,
        int maxNodes = DefaultMaxNodes)
    {
        var (root, count, _) = Build(content, cancellationToken, maxDepth, maxNodes, tolerant: false);
        return (root, count);
    }

    /// <summary>
    /// Like <see cref="Read"/>, but JSON that stops being valid after at least one value (a file cut off while being
    /// written, or with garbage at the end) still gives the tree built up to that point, with the reason. Content with no
    /// JSON value at all, or one that breaks a limit, still throws.
    /// </summary>
    public static (StructuredNode Root, int NodeCount, XmlStructureReader.ReadStop? Stop) ReadTolerant(
        byte[] content,
        CancellationToken cancellationToken = default,
        int maxDepth = DefaultMaxDepth,
        int maxNodes = DefaultMaxNodes) =>
        Build(content, cancellationToken, maxDepth, maxNodes, tolerant: true);

    private static (StructuredNode Root, int NodeCount, XmlStructureReader.ReadStop? Stop) Build(
        byte[] content, CancellationToken cancellationToken, int maxDepth, int maxNodes, bool tolerant)
    {
        var utf8 = ToUtf8(content);
        var lineStarts = FindLineStarts(utf8);

        var document = new StructuredNode(StructuredNodeKind.Document, null, null, 0, 1, null);
        var containers = new Stack<StructuredNode>();
        containers.Push(document);
        string? pendingName = null;
        var count = 0;
        XmlStructureReader.ReadStop? stop = null;

        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = maxDepth,
                AllowMultipleValues = true,
            });

        try
        {
            while (true)
            {
                try
                {
                    if (!reader.Read())
                    {
                        break;
                    }
                }
                catch (JsonException ex) when (tolerant && document.Children.Count > 0)
                {
                    var failedAt = ex.LineNumber is { } zeroBased ? (int)zeroBased + 1 : LineOf(lineStarts, reader.BytesConsumed);
                    stop = new XmlStructureReader.ReadStop(ex.Message, failedAt);
                    break;
                }

                if ((count & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var parent = containers.Peek();
                var line = LineOf(lineStarts, reader.TokenStartIndex);
                var name = parent.Kind == StructuredNodeKind.Object ? pendingName : null;
                var ordinal = parent.Children.Count;

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pendingName = reader.GetString();
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        var container = parent.AddChild(
                            reader.TokenType == JsonTokenType.StartObject ? StructuredNodeKind.Object : StructuredNodeKind.Array,
                            name, null, ordinal, line);
                        Count(ref count, maxNodes);
                        containers.Push(container);
                        pendingName = null;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        containers.Pop();
                        break;

                    case JsonTokenType.String:
                        parent.AddChild(StructuredNodeKind.String, name, reader.GetString(), ordinal, line);
                        Count(ref count, maxNodes);
                        pendingName = null;
                        break;

                    case JsonTokenType.Number:
                        parent.AddChild(StructuredNodeKind.Number, name, Encoding.UTF8.GetString(reader.ValueSpan), ordinal, line);
                        Count(ref count, maxNodes);
                        pendingName = null;
                        break;

                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        parent.AddChild(StructuredNodeKind.Boolean, name, reader.TokenType == JsonTokenType.True ? "true" : "false", ordinal, line);
                        Count(ref count, maxNodes);
                        pendingName = null;
                        break;

                    case JsonTokenType.Null:
                        parent.AddChild(StructuredNodeKind.Null, name, "null", ordinal, line);
                        Count(ref count, maxNodes);
                        pendingName = null;
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not valid JSON: {ex.Message}", ex);
        }

        if (document.Children.Count == 0)
        {
            throw new InvalidDataException("The file contains no JSON value.");
        }

        return document.Children.Count == 1 ? (document.PromoteOnlyChild(), count, stop) : (document, count, stop);
    }

    private static void Count(ref int count, int max)
    {
        if (++count > max)
        {
            throw new InvalidDataException($"The JSON has more than {max:N0} nodes.");
        }
    }

    /// <summary>Normalises the encoding: strips a UTF-8 byte-order mark and converts UTF-16 or UTF-32 to UTF-8.</summary>
    internal static byte[] ToUtf8(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return content[3..];
        }

        if (content.Length >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0 && content[3] == 0)
        {
            return Encoding.UTF8.GetBytes(Encoding.UTF32.GetString(content, 4, content.Length - 4));
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(content, 2, content.Length - 2));
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2));
        }

        return content;
    }

    private static int[] FindLineStarts(byte[] utf8)
    {
        var starts = new List<int> { 0 };
        var span = utf8.AsSpan();
        var offset = 0;
        while (true)
        {
            var next = span[offset..].IndexOf((byte)'\n');
            if (next < 0)
            {
                break;
            }

            offset += next + 1;
            starts.Add(offset);
        }

        return starts.ToArray();
    }

    // One-based line containing the byte offset.
    private static int LineOf(int[] lineStarts, long offset)
    {
        var index = Array.BinarySearch(lineStarts, (int)Math.Min(offset, int.MaxValue));
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}
