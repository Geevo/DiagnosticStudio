using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Documents;
using static DiagnosticStudio.Parsers.Etl.EtlNative;

namespace DiagnosticStudio.Parsers.Etl;

/// <summary>What the trace API could say about one event.</summary>
internal sealed record DecodedEtlEvent(
    string? ProviderName,
    string? TaskName,
    string? OpcodeName,
    string? Message,
    IReadOnlyList<EventDataItem> Data,
    bool HasSchema);

/// <summary>
/// Turns the payload of one trace event into named values with the trace API (tdh.dll), using the event's own schema
/// (TraceLogging) or the manifest registered on this machine. Events whose schema is not available here (traces written
/// by WPP need the program's symbol files) keep their raw payload, shown as readable strings and bytes. Never throws
/// for malformed content: whatever could be read is returned.
/// </summary>
internal sealed unsafe partial class EtlEventDecoder
{
    private const int MaxInfoBytes = 1 << 20;
    private const int MaxProperties = 4096;
    private const int MaxArrayElements = 1024;
    private const int MaxValueChars = 4000;
    private const int MaxPayloadHexBytes = 96;

    private byte[] _info = new byte[8192];
    private char[] _text = new char[2048];
    private readonly Dictionary<string, byte[]?> _maps = new(StringComparer.Ordinal);

    public DecodedEtlEvent Decode(EventRecord* record)
    {
        try
        {
            return DecodeWithSchema(record) ?? DecodeRaw(record);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or InsufficientMemoryException)
        {
            return DecodeRaw(record);
        }
    }

    private DecodedEtlEvent? DecodeWithSchema(EventRecord* record)
    {
        uint size = (uint)_info.Length;
        int rc;
        while (true)
        {
            fixed (byte* buffer = _info)
            {
                rc = TdhGetEventInformation(record, 0, null, buffer, &size);
            }

            if (rc == ErrorInsufficientBuffer && size <= MaxInfoBytes && size > _info.Length)
            {
                _info = new byte[size];
                continue;
            }

            break;
        }

        if (rc != ErrorSuccess || size < PropertyArrayOffset)
        {
            return null;
        }

        fixed (byte* info = _info)
        {
            var propertyCount = *(uint*)(info + InfoPropertyCount);
            var topLevel = *(uint*)(info + InfoTopLevelPropertyCount);
            if (propertyCount > MaxProperties || topLevel > propertyCount
                || PropertyArrayOffset + (propertyCount * (long)sizeof(EventPropertyInfo)) > size)
            {
                return null;
            }

            var state = new PropertyState(record, info, size, propertyCount, this);
            for (var i = 0u; i < topLevel && state.Ok; i++)
            {
                state.FormatProperty((int)i, prefix: null, topLevel: true);
            }

            // A TraceLogging event carries its own name, which says more than its (always zero) id.
            if (*(uint*)(info + InfoDecodingSource) == DecodingSourceTlg
                && StringAt(info, size, *(uint*)(info + InfoEventNameOffset)) is { } eventName)
            {
                state.Items.Insert(0, new EventDataItem("EventName", eventName));
            }

            if (!state.Ok)
            {
                state.Items.Add(new EventDataItem("(note)", "The rest of the data could not be read."));
            }

            var message = Substitute(StringAt(info, size, *(uint*)(info + InfoEventMessageOffset)), state.TopLevelValues);
            return new DecodedEtlEvent(
                StringAt(info, size, *(uint*)(info + InfoProviderNameOffset)),
                StringAt(info, size, *(uint*)(info + InfoTaskNameOffset)),
                StringAt(info, size, *(uint*)(info + InfoOpcodeNameOffset)),
                message,
                state.Items,
                HasSchema: true);
        }
    }

    // ---- values that were read for one event ----

    private sealed class PropertyState
    {
        private readonly EventRecord* _record;
        private readonly byte* _info;
        private readonly uint _infoSize;
        private readonly uint _propertyCount;
        private readonly EtlEventDecoder _owner;
        private readonly ulong[] _numbers;
        private byte* _data;
        private int _remaining;

        public PropertyState(EventRecord* record, byte* info, uint infoSize, uint propertyCount, EtlEventDecoder owner)
        {
            _record = record;
            _info = info;
            _infoSize = infoSize;
            _propertyCount = propertyCount;
            _owner = owner;
            _numbers = new ulong[propertyCount];
            _data = record->UserData;
            _remaining = record->UserData is null ? 0 : record->UserDataLength;
        }

        public bool Ok { get; private set; } = true;

        public List<EventDataItem> Items { get; } = new();

        public List<string> TopLevelValues { get; } = new();

        private EventPropertyInfo* Property(int index) =>
            (EventPropertyInfo*)(_info + PropertyArrayOffset) + index;

        public void FormatProperty(int index, string? prefix, bool topLevel)
        {
            if (!Ok || index < 0 || index >= _propertyCount)
            {
                Ok = false;
                return;
            }

            var property = Property(index);
            var flags = property->Flags;
            if ((flags & PropertyWbemXmlFragment) != 0)
            {
                Ok = false;
                return;
            }

            var name = StringAt(_info, _infoSize, property->NameOffset) ?? ("Property " + index);
            var fullName = prefix is null ? name : prefix + "." + name;

            int count = property->Count;
            var isArray = false;
            if ((flags & PropertyParamCount) != 0)
            {
                count = property->Count < _propertyCount ? (int)Math.Min(_numbers[property->Count], MaxArrayElements * 4UL) : 0;
                isArray = true;
            }
            else if (count > 1)
            {
                isArray = true;
            }
            else if (count == 0)
            {
                count = 1;
            }

            if (count > MaxArrayElements)
            {
                count = MaxArrayElements;
            }

            if ((flags & PropertyStruct) != 0)
            {
                for (var element = 0; element < count && Ok; element++)
                {
                    var elementName = isArray ? fullName + "[" + element + "]" : fullName;
                    for (var member = 0; member < property->StructMemberCount && Ok; member++)
                    {
                        FormatProperty(property->StructStartIndex + member, elementName, topLevel: false);
                    }
                }

                if (topLevel)
                {
                    TopLevelValues.Add(string.Empty);
                }

                return;
            }

            var length = property->Length;
            if ((flags & PropertyParamLength) != 0)
            {
                length = property->Length < _propertyCount ? (ushort)Math.Min(_numbers[property->Length], ushort.MaxValue) : (ushort)0;
            }

            if (property->InType == InTypeBinary && property->OutType == OutTypeIpv6 && length == 0)
            {
                length = 16;
            }

            var values = new List<string>(isArray ? Math.Min(count, 16) : 1);
            for (var element = 0; element < count && Ok; element++)
            {
                values.Add(FormatOne(property, index, length));
            }

            var value = isArray ? "[" + string.Join(", ", values) + "]" : values.Count > 0 ? values[0] : string.Empty;
            Items.Add(new EventDataItem(fullName, value));
            if (topLevel)
            {
                TopLevelValues.Add(value);
            }
        }

        private string FormatOne(EventPropertyInfo* property, int index, ushort length)
        {
            var inType = property->InType;
            var pointerSize = (_record->Flags & HeaderFlag32Bit) != 0 ? 4u : 8u;

            // Remember what a later property may use as its length or count.
            _numbers[index] = ReadNumber(inType);

            byte[]? map = property->MapNameOffset != 0 ? _owner.MapFor(_record, StringAt(_info, _infoSize, property->MapNameOffset)) : null;

            var buffer = _owner._text;
            while (true)
            {
                uint bytes = (uint)(buffer.Length * sizeof(char));
                ushort consumed = 0;
                int rc;
                fixed (char* text = buffer)
                fixed (byte* mapInfo = map)
                {
                    rc = TdhFormatProperty(
                        _info,
                        map is null ? null : mapInfo,
                        pointerSize,
                        inType,
                        property->OutType,
                        length,
                        (ushort)Math.Min(_remaining, ushort.MaxValue),
                        _data,
                        &bytes,
                        text,
                        &consumed);

                    if (rc == ErrorSuccess)
                    {
                        _data += consumed;
                        _remaining -= consumed;
                        var result = Marshal.PtrToStringUni((IntPtr)text) ?? string.Empty;
                        return Clean(result);
                    }
                }

                if (rc == ErrorInsufficientBuffer && bytes > buffer.Length * sizeof(char) && bytes <= MaxValueChars * 8)
                {
                    buffer = _owner._text = new char[(bytes / sizeof(char)) + 1];
                    continue;
                }

                // A value map that does not fit the data is dropped and the number shown as it is.
                if (map is not null)
                {
                    map = null;
                    continue;
                }

                // The remaining data cannot be read as described; what was read so far is kept.
                Ok = false;
                return "(unreadable)";
            }
        }

        private ulong ReadNumber(ushort inType)
        {
            var width = inType switch
            {
                InTypeInt8 or InTypeUInt8 => 1,
                InTypeInt16 or InTypeUInt16 => 2,
                InTypeInt32 or InTypeUInt32 or InTypeHexInt32 => 4,
                InTypeInt64 or InTypeUInt64 or InTypeHexInt64 => 8,
                _ => 0,
            };
            if (width == 0 || _remaining < width || _data is null)
            {
                return 0;
            }

            ulong value = 0;
            for (var i = width - 1; i >= 0; i--)
            {
                value = (value << 8) | _data[i];
            }

            return value;
        }
    }

    private byte[]? MapFor(EventRecord* record, string? mapName)
    {
        if (mapName is null)
        {
            return null;
        }

        var key = record->ProviderId.ToString() + "/" + mapName;
        if (_maps.TryGetValue(key, out var cached))
        {
            return cached;
        }

        byte[]? map = null;
        uint size = 512;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new byte[size];
            int rc;
            fixed (char* name = mapName)
            fixed (byte* b = buffer)
            {
                rc = TdhGetEventMapInformation(record, name, b, &size);
            }

            if (rc == ErrorSuccess)
            {
                map = buffer;
                break;
            }

            if (rc != ErrorInsufficientBuffer || size > MaxInfoBytes)
            {
                break;
            }
        }

        if (_maps.Count < 2048)
        {
            _maps[key] = map;
        }

        return map;
    }

    // ---- events without a schema ----

    private static DecodedEtlEvent DecodeRaw(EventRecord* record)
    {
        var length = record->UserData is null ? 0 : record->UserDataLength;
        var data = new List<EventDataItem>();
        if (length > 0)
        {
            var bytes = new ReadOnlySpan<byte>(record->UserData, length);
            var text = ReadableStrings(bytes);
            if (text.Length > 0)
            {
                data.Add(new EventDataItem("Text", text));
            }

            var shown = Math.Min(length, MaxPayloadHexBytes);
            var hex = Convert.ToHexString(bytes[..shown]);
            data.Add(new EventDataItem(
                "Payload (" + length + " bytes)",
                string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2))) + (length > shown ? " …" : string.Empty)));
        }

        return new DecodedEtlEvent(null, null, null, null, data, HasSchema: false);
    }

    /// <summary>Runs of printable text in the payload (ASCII or UTF-16), which is where WPP and similar traces keep their messages.</summary>
    internal static string ReadableStrings(ReadOnlySpan<byte> bytes)
    {
        var found = new List<string>();
        var total = 0;
        var i = 0;
        while (i < bytes.Length && found.Count < 24 && total < 1500)
        {
            // UTF-16 run of printable characters.
            var j = i;
            var sb = new StringBuilder();
            while (j + 1 < bytes.Length && bytes[j + 1] == 0 && IsPrintable(bytes[j]))
            {
                sb.Append((char)bytes[j]);
                j += 2;
            }

            if (sb.Length >= 4)
            {
                found.Add(sb.ToString());
                total += sb.Length;
                i = j;
                continue;
            }

            // ASCII run.
            j = i;
            sb.Clear();
            while (j < bytes.Length && IsPrintable(bytes[j]))
            {
                sb.Append((char)bytes[j]);
                j++;
            }

            if (sb.Length >= 5)
            {
                found.Add(sb.ToString());
                total += sb.Length;
                i = j;
                continue;
            }

            i++;
        }

        return string.Join(" | ", found);
    }

    private static bool IsPrintable(byte b) => b is >= 0x20 and < 0x7F;

    // ---- text ----

    private static string? StringAt(byte* info, uint size, uint offset)
    {
        if (offset == 0 || offset >= size)
        {
            return null;
        }

        var max = (int)((size - offset) / sizeof(char));
        var chars = (char*)(info + offset);
        var length = 0;
        while (length < max && chars[length] != '\0')
        {
            length++;
        }

        return length == 0 ? null : new string(chars, 0, length).Trim();
    }

    private static string Clean(string value)
    {
        if (value.Length > MaxValueChars)
        {
            value = value[..MaxValueChars] + "…";
        }

        return value;
    }

    /// <summary>Fills the numbered places (%1, %2, ...) of a message with the values read from the event.</summary>
    internal static string? Substitute(string? template, IReadOnlyList<string> values)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var text = Placeholder().Replace(template, match =>
        {
            var n = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return n >= 1 && n <= values.Count ? values[n - 1] : match.Value;
        });
        text = text.Replace("%%", "%", StringComparison.Ordinal).Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"%(\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
