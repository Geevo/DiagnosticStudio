using System.Text;
using System.Text.Json;
using System.Xml;

namespace DiagnosticStudio.Parsers.Structured;

/// <summary>
/// Re-indents JSON and XML for reading, for files written on one line or with no indentation. It changes layout only:
/// JSON strings, property names and numbers are copied exactly as written (escapes included, nothing rounded). XML
/// whitespace that only separated tags is replaced by indentation; text, CDATA, comments and attributes are kept. The
/// original file is always still available; this produces a second, derived view.
/// </summary>
public static class StructuredFormatter
{
    public const string IndentUnit = "  ";

    /// <exception cref="InvalidDataException">The content is not valid JSON.</exception>
    public static string FormatJson(byte[] content, CancellationToken cancellationToken = default)
    {
        var utf8 = JsonStructureReader.ToUtf8(content);
        var output = new StringBuilder(utf8.Length + (utf8.Length / 2));

        // One entry per open object or array: whether it has had an item yet.
        var hasItems = new Stack<bool>();
        var pendingComments = new List<string>();
        var afterName = false;
        var topLevelValues = 0;
        var tokens = 0;

        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Allow,
                AllowTrailingCommas = true,
                MaxDepth = JsonStructureReader.DefaultMaxDepth,
                AllowMultipleValues = true,
            });

        try
        {
            while (reader.Read())
            {
                if ((++tokens & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.Comment:
                        pendingComments.Add(CommentText(reader));
                        break;

                    case JsonTokenType.PropertyName:
                        BeginItem(output, hasItems, pendingComments);
                        output.Append('"').Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append("\": ");
                        afterName = true;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        var had = hasItems.Pop();
                        if (pendingComments.Count > 0)
                        {
                            // Comments after the last item sit inside the container, one level in.
                            FlushComments(output, hasItems.Count + 1, pendingComments);
                            had = true;
                        }

                        if (had)
                        {
                            NewLine(output, hasItems.Count);
                        }

                        output.Append(reader.TokenType == JsonTokenType.EndObject ? '}' : ']');
                        break;

                    default:
                        if (!afterName)
                        {
                            if (hasItems.Count == 0)
                            {
                                // Several top-level values (JSON Lines): a blank line between them.
                                if (topLevelValues++ > 0)
                                {
                                    output.Append("\n\n");
                                }

                                FlushTopLevelComments(output, pendingComments);
                            }
                            else
                            {
                                BeginItem(output, hasItems, pendingComments);
                            }
                        }

                        afterName = false;
                        AppendValue(output, ref reader, hasItems);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not valid JSON: " + ex.Message, ex);
        }

        if (pendingComments.Count > 0)
        {
            FlushTopLevelComments(output, pendingComments);
        }

        return output.ToString();
    }

    private static void AppendValue(StringBuilder output, ref Utf8JsonReader reader, Stack<bool> hasItems)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                output.Append('{');
                hasItems.Push(false);
                break;
            case JsonTokenType.StartArray:
                output.Append('[');
                hasItems.Push(false);
                break;
            case JsonTokenType.String:
                output.Append('"').Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append('"');
                break;
            default:
                // Numbers, true, false and null are written as they appear.
                output.Append(Encoding.UTF8.GetString(reader.ValueSpan));
                break;
        }
    }

    /// <summary>Starts the next member or element: the comma after the previous one, comments waiting to be placed, and the indentation.</summary>
    private static void BeginItem(StringBuilder output, Stack<bool> hasItems, List<string> pendingComments)
    {
        var depth = hasItems.Count;
        if (hasItems.Pop())
        {
            output.Append(',');
        }

        hasItems.Push(true);
        FlushComments(output, depth, pendingComments);
        NewLine(output, depth);
    }

    private static void FlushComments(StringBuilder output, int depth, List<string> comments)
    {
        foreach (var comment in comments)
        {
            NewLine(output, depth);
            output.Append(comment);
        }

        comments.Clear();
    }

    private static void FlushTopLevelComments(StringBuilder output, List<string> comments)
    {
        foreach (var comment in comments)
        {
            output.Append(comment).Append('\n');
        }

        comments.Clear();
    }

    private static string CommentText(Utf8JsonReader reader)
    {
        var text = Encoding.UTF8.GetString(reader.ValueSpan);

        // The reader gives the comment without its delimiters; a comment that spans lines was a block comment.
        return text.Contains('\n') ? "/*" + text + "*/" : "//" + text;
    }

    private static void NewLine(StringBuilder output, int depth)
    {
        output.Append('\n');
        for (var i = 0; i < depth; i++)
        {
            output.Append(IndentUnit);
        }
    }

    /// <exception cref="InvalidDataException">The content is not well-formed XML.</exception>
    public static string FormatXml(byte[] content, CancellationToken cancellationToken = default)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Fragment,
            IgnoreWhitespace = true,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            CheckCharacters = false,
        };
        var writerSettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = IndentUnit,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.None,
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            CheckCharacters = false,
        };

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = XmlReader.Create(stream, readerSettings);
            var text = new StringBuilder(content.Length + (content.Length / 2));

            using (var writer = XmlWriter.Create(text, writerSettings))
            {
                // The declaration is kept as written; the writer would substitute its own encoding.
                reader.Read();
                if (reader.NodeType == XmlNodeType.XmlDeclaration)
                {
                    text.Append("<?xml ").Append(reader.Value).Append("?>\n");
                    reader.Read();
                }

                var nodes = 0;
                while (!reader.EOF)
                {
                    if ((++nodes & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    writer.WriteNode(reader, defattr: true);
                }
            }

            return text.ToString();
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Not well-formed XML: " + ex.Message, ex);
        }
    }
}
