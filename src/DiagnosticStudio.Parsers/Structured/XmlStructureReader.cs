using System.Xml;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers.Structured;

/// <summary>
/// Builds a <see cref="StructuredNode"/> tree from XML. The reader is deliberately defensive: DTDs are ignored and no
/// resolver is set, so nothing outside the file is ever fetched and entity expansion cannot blow up; nesting depth and
/// node count are capped; the tree is built with an explicit stack, so hostile nesting cannot overflow the call stack.
/// Several top-level elements (a fragment) are accepted.
/// </summary>
public static class XmlStructureReader
{
    public const int DefaultMaxDepth = 512;
    public const int DefaultMaxNodes = 2_000_000;

    /// <returns>A document node whose children are the top-level elements, and the number of nodes created.</returns>
    /// <exception cref="InvalidDataException">The content is not well-formed XML, or exceeds the limits.</exception>
    public static (StructuredNode Root, int NodeCount) Read(
        Stream stream,
        CancellationToken cancellationToken = default,
        int maxDepth = DefaultMaxDepth,
        int maxNodes = DefaultMaxNodes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Fragment,
            IgnoreWhitespace = true,
            IgnoreComments = false,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
            CloseInput = false,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            return Build(reader, cancellationToken, maxDepth, maxNodes);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Not well-formed XML: {ex.Message}", ex);
        }
    }

    private static (StructuredNode, int) Build(XmlReader reader, CancellationToken cancellationToken, int maxDepth, int maxNodes)
    {
        var lineInfo = reader as IXmlLineInfo;
        var document = new StructuredNode(StructuredNodeKind.Document, null, null, 0, 1, null);
        var frames = new Stack<Frame>();
        frames.Push(new Frame(document));
        var count = 0;

        while (reader.Read())
        {
            if ((count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var frame = frames.Peek();
            var line = lineInfo?.LineNumber ?? 0;

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var element = frame.Node.AddChild(StructuredNodeKind.Element, reader.Name, null, frame.Next("e:" + reader.Name), line);
                    Count(ref count, maxNodes);

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            element.AddChild(StructuredNodeKind.Attribute, reader.Name, reader.Value, 1, lineInfo?.LineNumber ?? line);
                            Count(ref count, maxNodes);
                        }

                        reader.MoveToElement();
                    }

                    if (!reader.IsEmptyElement)
                    {
                        if (frames.Count > maxDepth)
                        {
                            throw new InvalidDataException($"XML is nested deeper than {maxDepth:N0} levels.");
                        }

                        frames.Push(new Frame(element));
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (frames.Count > 1)
                    {
                        frames.Pop();
                    }

                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    frame.Node.AddChild(StructuredNodeKind.Text, null, reader.Value, frame.Next("t"), line);
                    Count(ref count, maxNodes);
                    break;

                case XmlNodeType.Comment:
                    frame.Node.AddChild(StructuredNodeKind.Comment, null, reader.Value, frame.Next("c"), line);
                    Count(ref count, maxNodes);
                    break;
            }
        }

        // Fragment mode also accepts bare text, so require at least one real element: prose is not XML.
        if (!document.Children.Any(c => c.Kind == StructuredNodeKind.Element))
        {
            throw new InvalidDataException("The file contains no XML elements.");
        }

        return (document, count);
    }

    private static void Count(ref int count, int max)
    {
        if (++count > max)
        {
            throw new InvalidDataException($"The XML has more than {max:N0} nodes.");
        }
    }

    private sealed class Frame
    {
        private Dictionary<string, int>? _counts;

        public Frame(StructuredNode node) => Node = node;

        public StructuredNode Node { get; }

        public int Next(string key)
        {
            _counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            _counts.TryGetValue(key, out var n);
            _counts[key] = ++n;
            return n;
        }
    }
}
