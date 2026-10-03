using System.Globalization;
using System.Text;

namespace DiagnosticStudio.Parsers.Evtx;

/// <summary>Raised for structurally invalid event data. Always caught at the record boundary.</summary>
internal sealed class EvtxFormatException : Exception
{
    public EvtxFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>Minimal XML tree produced from binary XML. Text is kept as plain strings.</summary>
internal sealed class XmlElem
{
    public XmlElem(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<KeyValuePair<string, string>> Attributes { get; } = new();

    /// <summary>Child <see cref="XmlElem"/>s and <see cref="string"/> text runs, in document order.</summary>
    public List<object> Content { get; } = new();

    public IEnumerable<XmlElem> Elements => Content.OfType<XmlElem>();

    public string Text
    {
        get
        {
            if (Content.Count == 1 && Content[0] is string single)
            {
                return single;
            }

            var sb = new StringBuilder();
            foreach (var part in Content)
            {
                if (part is string s)
                {
                    sb.Append(s);
                }
            }

            return sb.ToString();
        }
    }

    public string? Attribute(string name)
    {
        foreach (var (key, value) in Attributes)
        {
            if (key == name)
            {
                return value;
            }
        }

        return null;
    }

    public XmlElem? Child(string name) => Elements.FirstOrDefault(e => e.Name == name);
}

/// <summary>
/// Decodes the binary XML of event records within a single 64 KiB chunk. Names and templates are referenced by
/// chunk-relative offsets, so a decoder is bound to one chunk's bytes. Every read is bounds-checked and nesting
/// and size are limited, so a hostile record can fail but cannot loop, recurse unboundedly or exhaust memory.
/// </summary>
internal sealed class BinXmlDecoder
{
    private const int MaxDepth = 64;
    private const int MaxNodesPerRecord = 50_000;
    private const int ElementHeader = 1 + 2 + 4; // token, dependency id, data size
    private const int CompactElementHeader = 1 + 4; // token, data size

    private readonly byte[] _c;
    private int _depth;
    private int _nodes;
    private int _elementHeader = ElementHeader;
    private bool _omittedOptional;
    private List<string>? _arrayItems;
    private List<XmlElem>? _pendingSiblings;

    public BinXmlDecoder(byte[] chunk)
    {
        _c = chunk;
    }

    private readonly record struct Slot(byte Type, int Offset, int Size);

    /// <summary>Decodes the binary XML stream in <c>chunk[start..end)</c> into a document element.</summary>
    public XmlElem DecodeRecord(int start, int end)
    {
        _depth = 0;
        _nodes = 0;
        _elementHeader = ElementHeader;
        var document = new XmlElem("#document");
        var pos = start;
        ReadNodes(ref pos, end, document, null, stopAtEndElement: false);
        return document.Elements.FirstOrDefault()
            ?? throw new EvtxFormatException("Record contains no element.");
    }

    // ---- node stream ----

    private void ReadNodes(ref int pos, int end, XmlElem container, Slot[]? subs, bool stopAtEndElement)
    {
        while (pos < end)
        {
            var token = _c[Check(pos, 1)];
            switch (token & 0x3F)
            {
                case 0x00: // end of stream
                    pos++;
                    return;

                case 0x01: // open start element
                    var element = ReadElement(ref pos, end, subs);
                    if (element is not null)
                    {
                        container.Content.Add(element);
                    }

                    if (_pendingSiblings is not null)
                    {
                        container.Content.AddRange(_pendingSiblings);
                        _pendingSiblings = null;
                    }

                    break;

                case 0x04: // end element
                    pos++;
                    if (stopAtEndElement)
                    {
                        return;
                    }

                    break;

                case 0x05: // inline value
                    pos++;
                    Add(container, ReadInlineValue(ref pos));
                    break;

                case 0x07: // CDATA
                    pos++;
                    Add(container, ReadUtf16WithCount(ref pos));
                    break;

                case 0x08: // character reference
                    pos++;
                    Add(container, ((char)U16(pos)).ToString());
                    pos += 2;
                    break;

                case 0x09: // entity reference
                    pos++;
                    Add(container, EntityText(ReadName(ref pos)));
                    break;

                case 0x0C: // template instance
                    ReadTemplateInstance(ref pos, container);
                    break;

                case 0x0D: // normal substitution
                case 0x0E: // optional substitution
                    var slot = ReadSubstitution(ref pos, subs, out var optional);
                    if (!AppendSubstitution(container, slot, optional))
                    {
                        _omittedOptional = true;
                    }

                    break;

                case 0x0F: // fragment header: token, major, minor, flags
                    pos += 4;
                    break;

                default:
                    throw new EvtxFormatException($"Unexpected binary XML token 0x{token:x2} at offset {pos}.");
            }
        }
    }

    private void Add(XmlElem container, string? text)
    {
        if (text is null)
        {
            return;
        }

        CountNode();
        container.Content.Add(text);
    }

    private void CountNode()
    {
        if (++_nodes > MaxNodesPerRecord)
        {
            throw new EvtxFormatException("Record exceeds the node limit.");
        }
    }

    private XmlElem? ReadElement(ref int pos, int end, Slot[]? subs)
    {
        if (++_depth > MaxDepth)
        {
            throw new EvtxFormatException("Element nesting is too deep.");
        }

        CountNode();
        var hasAttributes = (_c[pos] & 0x40) != 0;
        pos += _elementHeader;
        var element = new XmlElem(ReadName(ref pos));

        if (hasAttributes)
        {
            var attributeBytes = (int)U32(pos);
            pos += 4;
            var attributesEnd = pos + attributeBytes;
            if (attributesEnd > end || attributeBytes < 0)
            {
                throw new EvtxFormatException("Attribute list overruns the record.");
            }

            ReadAttributes(ref pos, attributesEnd, element, subs);
        }

        var closeToken = _c[Check(pos, 1)];
        pos++;
        switch (closeToken)
        {
            case 0x02: // close start element: content follows
                var outerOmitted = _omittedOptional;
                var outerArray = _arrayItems;
                _omittedOptional = false;
                _arrayItems = null;
                ReadNodes(ref pos, end, element, subs, stopAtEndElement: true);
                var arrayItems = _arrayItems;
                var wasOmitted = _omittedOptional;
                _omittedOptional = outerOmitted;
                _arrayItems = outerArray;

                if (arrayItems is not null)
                {
                    // An array value in an element's content repeats the element, once per item.
                    element.Content.Clear();
                    if (arrayItems.Count == 0)
                    {
                        // Empty (non-optional) array: the element is still present, with no content.
                        break;
                    }

                    element.Content.Add(arrayItems[0]);
                    if (arrayItems.Count > 1)
                    {
                        _pendingSiblings = new List<XmlElem>();
                        for (var i = 1; i < arrayItems.Count; i++)
                        {
                            var copy = new XmlElem(element.Name);
                            copy.Attributes.AddRange(element.Attributes);
                            copy.Content.Add(arrayItems[i]);
                            _pendingSiblings.Add(copy);
                        }
                    }

                    break;
                }

                // An element whose only content was an empty optional value is not emitted (matches the OS rendering).
                if (element.Content.Count == 0 && element.Attributes.Count == 0 && wasOmitted)
                {
                    _depth--;
                    return null;
                }

                break;
            case 0x03: // close empty element
                break;
            default:
                throw new EvtxFormatException($"Unexpected token 0x{closeToken:x2} closing element start at offset {pos - 1}.");
        }

        _depth--;
        return element;
    }

    private void ReadAttributes(ref int pos, int attributesEnd, XmlElem element, Slot[]? subs)
    {
        while (pos < attributesEnd)
        {
            var token = _c[pos];
            if ((token & 0x3F) != 0x06)
            {
                throw new EvtxFormatException($"Unexpected token 0x{token:x2} in attribute list at offset {pos}.");
            }

            pos++;
            var name = ReadName(ref pos);
            var value = new StringBuilder();
            var omitted = false;
            var any = false;

            // An attribute value is one or more value tokens up to the next attribute.
            while (pos < attributesEnd)
            {
                var valueToken = _c[pos] & 0x3F;
                if (valueToken == 0x05)
                {
                    pos++;
                    value.Append(ReadInlineValue(ref pos));
                }
                else if (valueToken is 0x0D or 0x0E)
                {
                    var slot = ReadSubstitution(ref pos, subs, out var optional);
                    if (optional && slot.Size == 0)
                    {
                        omitted = true;
                    }
                    else
                    {
                        value.Append(RenderValue(slot));
                    }
                }
                else if (valueToken == 0x08)
                {
                    pos++;
                    value.Append((char)U16(pos));
                    pos += 2;
                }
                else if (valueToken == 0x09)
                {
                    pos++;
                    value.Append(EntityText(ReadName(ref pos)));
                }
                else
                {
                    break;
                }

                any = true;
            }

            if (!any)
            {
                throw new EvtxFormatException("Attribute has no value.");
            }

            if (!omitted)
            {
                element.Attributes.Add(new KeyValuePair<string, string>(name, value.ToString()));
            }
        }
    }

    // ---- templates and substitutions ----

    private void ReadTemplateInstance(ref int pos, XmlElem container)
    {
        if (++_depth > MaxDepth)
        {
            throw new EvtxFormatException("Template nesting is too deep.");
        }

        pos += 1 + 1 + 4; // token, unknown, template id
        var definitionOffset = (int)U32(pos);
        pos += 4;

        int bodyStart;
        int bodyEnd;
        if (definitionOffset == pos)
        {
            // Definition is stored inline: next-definition offset, guid, data size, then the body.
            var size = (int)U32(pos + 20);
            bodyStart = pos + 24;
            bodyEnd = bodyStart + size;
            pos = bodyEnd;
        }
        else
        {
            var size = (int)U32(definitionOffset + 20);
            bodyStart = definitionOffset + 24;
            bodyEnd = bodyStart + size;
        }

        if (Overruns(bodyStart, bodyEnd))
        {
            throw new EvtxFormatException("Template definition overruns the chunk.");
        }

        var count = (int)U32(pos);
        pos += 4;
        if (count < 0 || count > 4096)
        {
            throw new EvtxFormatException($"Implausible substitution count {count}.");
        }

        var descriptors = new (int Size, byte Type)[count];
        for (var i = 0; i < count; i++)
        {
            descriptors[i] = (U16(pos), _c[Check(pos + 2, 1)]);
            pos += 4;
        }

        var slots = new Slot[count];
        for (var i = 0; i < count; i++)
        {
            Check(pos, descriptors[i].Size);
            slots[i] = new Slot(descriptors[i].Type, pos, descriptors[i].Size);
            pos += descriptors[i].Size;
        }

        var bodyPos = bodyStart;
        ReadNodes(ref bodyPos, bodyEnd, container, slots, stopAtEndElement: false);
        _depth--;
    }

    private bool Overruns(int start, int end) => start < 0 || end < start || end > _c.Length;

    /// <summary>
    /// Some providers (the certificate services client's, for one) write a nested fragment that starts straight with
    /// an element, whose header has no dependency id: token, data size, name. Told apart by the data size, which
    /// then spans exactly the rest of the value (one end-of-stream byte may follow) while, read the usual way, it
    /// would not.
    /// </summary>
    private bool LacksDependencyId(Slot slot)
    {
        if (slot.Size < ElementHeader || (_c[slot.Offset] & 0x3F) != 0x01)
        {
            return false;
        }

        bool Spans(int sizeAt, int header)
        {
            var rest = (long)slot.Size - header;
            var size = U32(sizeAt);
            return size == rest || size == rest - 1;
        }

        return Spans(slot.Offset + 1, CompactElementHeader) && !Spans(slot.Offset + 3, ElementHeader);
    }

    private Slot ReadSubstitution(ref int pos, Slot[]? subs, out bool optional)
    {
        optional = (_c[pos] & 0x3F) == 0x0E;
        var index = U16(pos + 1);
        pos += 1 + 2 + 1; // token, index, declared type
        if (subs is null || index >= subs.Length)
        {
            throw new EvtxFormatException($"Substitution {index} is outside the template values.");
        }

        return subs[index];
    }

    /// <returns><c>false</c> when an optional value was empty and nothing was emitted.</returns>
    private bool AppendSubstitution(XmlElem container, Slot slot, bool optional)
    {
        if (optional && slot.Size == 0)
        {
            return false;
        }

        if ((slot.Type & 0x7F) == 0x21)
        {
            // Nested binary XML fragment.
            var p = slot.Offset;
            if (++_depth > MaxDepth)
            {
                throw new EvtxFormatException("Nested fragment is too deep.");
            }

            var outerHeader = _elementHeader;
            _elementHeader = LacksDependencyId(slot) ? CompactElementHeader : ElementHeader;
            ReadNodes(ref p, slot.Offset + slot.Size, container, null, stopAtEndElement: false);
            _elementHeader = outerHeader;
            _depth--;
            return true;
        }

        if ((slot.Type & 0x80) != 0 && container.Name != "#document" && container.Content.Count == 0)
        {
            _arrayItems = ArrayItems(slot.Type & 0x7F, slot.Offset, slot.Size);
            return true;
        }

        Add(container, RenderValue(slot));
        return true;
    }

    // ---- names and inline values ----

    private string ReadName(ref int pos)
    {
        var offset = (int)U32(pos);
        pos += 4;
        var at = offset;
        var inline = offset == pos;
        var length = U16(at + 6);
        var name = Utf16(at + 8, length * 2);
        if (inline)
        {
            pos += 4 + 2 + 2 + ((length + 1) * 2);
        }

        return name;
    }

    private string ReadInlineValue(ref int pos)
    {
        var type = _c[Check(pos, 1)];
        pos++;
        switch (type)
        {
            case 0x01:
                return ReadUtf16WithCount(ref pos);
            default:
                throw new EvtxFormatException($"Unsupported inline value type 0x{type:x2} at offset {pos - 1}.");
        }
    }

    private string ReadUtf16WithCount(ref int pos)
    {
        var chars = U16(pos);
        pos += 2;
        var text = Utf16(pos, chars * 2);
        pos += chars * 2;
        return text;
    }

    private static string EntityText(string name) => name switch
    {
        "lt" => "<",
        "gt" => ">",
        "amp" => "&",
        "quot" => "\"",
        "apos" => "'",
        _ => "&" + name + ";",
    };

    // ---- value rendering ----

    private string RenderValue(Slot slot)
    {
        var o = slot.Offset;
        var n = slot.Size;
        var isArray = (slot.Type & 0x80) != 0;
        var type = slot.Type & 0x7F;

        if (isArray)
        {
            return RenderArray(type, o, n);
        }

        return RenderScalar(type, o, n);
    }

    private string RenderScalar(int type, int o, int n)
    {
        Check(o, n);
        switch (type)
        {
            case 0x00:
                return string.Empty;
            case 0x01:
                return TrimNul(Utf16(o, n - (n % 2)));
            case 0x02:
                return TrimNul(Encoding.Latin1.GetString(_c, o, n));
            case 0x03:
                return ((sbyte)_c[o]).ToString(CultureInfo.InvariantCulture);
            case 0x04:
                return _c[o].ToString(CultureInfo.InvariantCulture);
            case 0x05:
                return ((short)U16(o)).ToString(CultureInfo.InvariantCulture);
            case 0x06:
                return U16(o).ToString(CultureInfo.InvariantCulture);
            case 0x07:
                return ((int)U32(o)).ToString(CultureInfo.InvariantCulture);
            case 0x08:
                return U32(o).ToString(CultureInfo.InvariantCulture);
            case 0x09:
                return ((long)U64(o)).ToString(CultureInfo.InvariantCulture);
            case 0x0A:
                return U64(o).ToString(CultureInfo.InvariantCulture);
            case 0x0B:
                return BitConverter.ToSingle(_c, o).ToString("R", CultureInfo.InvariantCulture);
            case 0x0C:
                return BitConverter.ToDouble(_c, o).ToString("R", CultureInfo.InvariantCulture);
            case 0x0D:
                return U32(o) != 0 ? "true" : "false";
            case 0x0E:
                return Convert.ToHexString(_c, o, n);
            case 0x0F:
                return "{" + new Guid(_c.AsSpan(o, 16)).ToString() + "}"; // lower case, as the operating system renders it
            case 0x10: // size_t
                return n == 8 ? "0x" + U64(o).ToString("x", CultureInfo.InvariantCulture)
                              : "0x" + U32(o).ToString("x", CultureInfo.InvariantCulture);
            case 0x11:
                return FileTimeText(U64(o));
            case 0x12:
                return SystemTimeText(o);
            case 0x13:
                return SidText(o, n);
            case 0x14:
                return "0x" + U32(o).ToString("x", CultureInfo.InvariantCulture);
            case 0x15:
                return "0x" + U64(o).ToString("x", CultureInfo.InvariantCulture);
            default:
                return Convert.ToHexString(_c, o, n); // unknown: never lose the data
        }
    }

    private string RenderArray(int type, int o, int n) => string.Join(", ", ArrayItems(type, o, n));

    /// <summary>Splits an array value into its rendered items. String items keep their empty entries.</summary>
    private List<string> ArrayItems(int type, int o, int n)
    {
        Check(o, n);
        var parts = new List<string>();
        if (type == 0x01)
        {
            // UTF-16 strings, each terminated by a NUL.
            var text = Utf16(o, n - (n % 2));
            if (text.Length > 0)
            {
                parts.AddRange(text.Split('\0'));
                if (parts[^1].Length == 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }

            return parts;
        }

        var width = type switch
        {
            0x03 or 0x04 => 1,
            0x05 or 0x06 => 2,
            0x07 or 0x08 or 0x0B or 0x0D or 0x14 => 4,
            0x09 or 0x0A or 0x0C or 0x11 or 0x15 => 8,
            0x0F or 0x12 => 16,
            _ => 0,
        };

        if (width == 0)
        {
            parts.Add(Convert.ToHexString(_c, o, n));
            return parts;
        }

        for (var offset = 0; offset + width <= n; offset += width)
        {
            parts.Add(RenderScalar(type, o + offset, width));
        }

        return parts;
    }

    private static string TrimNul(string text)
    {
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    internal static string FileTimeText(ulong fileTime)
    {
        const ulong MaxFileTime = 2650467743999999999UL; // 9999-12-31T23:59:59.9999999Z
        return fileTime > MaxFileTime
            ? fileTime.ToString(CultureInfo.InvariantCulture)
            : DateTime.FromFileTimeUtc((long)fileTime).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private string SystemTimeText(int o)
    {
        try
        {
            var dt = new DateTime(U16(o), U16(o + 2), U16(o + 6), U16(o + 8), U16(o + 10), U16(o + 12), U16(o + 14), DateTimeKind.Utc);
            return dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Convert.ToHexString(_c, o, 16);
        }
    }

    private string SidText(int o, int n)
    {
        if (n < 8)
        {
            return Convert.ToHexString(_c, o, n);
        }

        var revision = _c[o];
        var subAuthorities = _c[o + 1];
        if (n < 8 + (subAuthorities * 4))
        {
            return Convert.ToHexString(_c, o, n);
        }

        ulong authority = 0;
        for (var i = 0; i < 6; i++)
        {
            authority = (authority << 8) | _c[o + 2 + i];
        }

        var sb = new StringBuilder();
        sb.Append("S-").Append(revision).Append('-').Append(authority);
        for (var i = 0; i < subAuthorities; i++)
        {
            sb.Append('-').Append(U32(o + 8 + (i * 4)));
        }

        return sb.ToString();
    }

    // ---- bounds-checked primitives ----

    private int Check(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > _c.Length - length)
        {
            throw new EvtxFormatException($"Read of {length} bytes at {offset} is outside the chunk.");
        }

        return offset;
    }

    private ushort U16(int offset)
    {
        Check(offset, 2);
        return BitConverter.ToUInt16(_c, offset);
    }

    private uint U32(int offset)
    {
        Check(offset, 4);
        return BitConverter.ToUInt32(_c, offset);
    }

    private ulong U64(int offset)
    {
        Check(offset, 8);
        return BitConverter.ToUInt64(_c, offset);
    }

    private string Utf16(int offset, int byteCount)
    {
        Check(offset, byteCount);
        return Encoding.Unicode.GetString(_c, offset, byteCount);
    }
}
