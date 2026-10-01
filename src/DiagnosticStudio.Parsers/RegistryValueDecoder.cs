using System.Globalization;
using System.Text;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers;

/// <summary>Turns raw registry value data into the typed, display-ready <see cref="RegistryValue"/>.</summary>
internal static class RegistryValueDecoder
{
    private const int DisplayHexBytes = 64;

    public static RegistryValue String(string name, bool isDefault, string text, int line) => new()
    {
        Name = name,
        IsDefault = isDefault,
        Kind = RegistryValueKind.String,
        TypeName = "REG_SZ",
        DisplayValue = text,
        SourceLine = line,
    };

    public static RegistryValue DWord(string name, bool isDefault, uint value, int line, bool bigEndian = false) => new()
    {
        Name = name,
        IsDefault = isDefault,
        Kind = bigEndian ? RegistryValueKind.DWordBigEndian : RegistryValueKind.DWord,
        TypeName = bigEndian ? "REG_DWORD_BIG_ENDIAN" : "REG_DWORD",
        DisplayValue = DWordText(value),
        DataLength = 4,
        SourceLine = line,
    };

    public static RegistryValue QWord(string name, bool isDefault, ulong value, int line) => new()
    {
        Name = name,
        IsDefault = isDefault,
        Kind = RegistryValueKind.QWord,
        TypeName = "REG_QWORD",
        DisplayValue = string.Create(CultureInfo.InvariantCulture, $"0x{value:x16} ({value})"),
        DataLength = 8,
        SourceLine = line,
    };

    public static RegistryValue Deleted(string name, bool isDefault, int line) => new()
    {
        Name = name,
        IsDefault = isDefault,
        Kind = RegistryValueKind.Deleted,
        TypeName = "(delete)",
        DisplayValue = "(value deleted)",
        SourceLine = line,
    };

    /// <summary>A value whose data could not be decoded. Keeps the original text so nothing is hidden.</summary>
    public static RegistryValue Undecodable(string name, bool isDefault, string rawText, int line) => new()
    {
        Name = name,
        IsDefault = isDefault,
        Kind = RegistryValueKind.Unknown,
        TypeName = "(unparsed)",
        DisplayValue = rawText,
        SourceLine = line,
    };

    /// <summary>Decodes the bytes of a <c>hex</c> / <c>hex(N)</c> value.</summary>
    public static RegistryValue FromBytes(string name, bool isDefault, int typeNumber, byte[] data, int line)
    {
        var kind = typeNumber is >= 0 and <= 11 ? (RegistryValueKind)typeNumber : RegistryValueKind.Unknown;
        var typeName = kind switch
        {
            RegistryValueKind.None => "REG_NONE",
            RegistryValueKind.String => "REG_SZ",
            RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
            RegistryValueKind.Binary => "REG_BINARY",
            RegistryValueKind.DWord => "REG_DWORD",
            RegistryValueKind.DWordBigEndian => "REG_DWORD_BIG_ENDIAN",
            RegistryValueKind.Link => "REG_LINK",
            RegistryValueKind.MultiString => "REG_MULTI_SZ",
            RegistryValueKind.ResourceList => "REG_RESOURCE_LIST",
            RegistryValueKind.FullResourceDescriptor => "REG_FULL_RESOURCE_DESCRIPTOR",
            RegistryValueKind.ResourceRequirementsList => "REG_RESOURCE_REQUIREMENTS_LIST",
            RegistryValueKind.QWord => "REG_QWORD",
            _ => string.Create(CultureInfo.InvariantCulture, $"REG_UNKNOWN (0x{typeNumber:x})"),
        };

        string display;
        string? detail = null;
        bool keepBytes = false;
        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
            case RegistryValueKind.Link:
                display = DecodeUtf16UntilNul(data);
                break;

            case RegistryValueKind.MultiString:
                var parts = SplitMultiString(data).ToArray();
                display = string.Join(" | ", parts);
                detail = string.Join(Environment.NewLine, parts);
                break;

            case RegistryValueKind.DWord when data.Length == 4:
                display = DWordText(BitConverter.ToUInt32(data));
                break;

            case RegistryValueKind.DWordBigEndian when data.Length == 4:
                display = DWordText(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data));
                break;

            case RegistryValueKind.QWord when data.Length == 8:
                display = string.Create(
                    CultureInfo.InvariantCulture,
                    $"0x{BitConverter.ToUInt64(data):x16} ({BitConverter.ToUInt64(data)})");
                break;

            default:
                // BINARY, NONE, resource types, and numeric types with an unexpected length: show the bytes.
                display = HexPreview(data);
                keepBytes = true;
                break;
        }

        var stored = keepBytes && data.Length > 0
            ? data.Length <= RegistryValue.MaxStoredDataBytes ? data : data[..RegistryValue.MaxStoredDataBytes]
            : null;

        return new RegistryValue
        {
            Name = name,
            IsDefault = isDefault,
            Kind = kind,
            TypeName = typeName,
            DisplayValue = display,
            DetailText = detail,
            DataLength = data.Length,
            Data = stored,
            IsDataTruncated = stored is not null && data.Length > RegistryValue.MaxStoredDataBytes,
            SourceLine = line,
        };
    }

    private static string DWordText(uint value) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{value:x8} ({value})");

    private static string DecodeUtf16UntilNul(byte[] data)
    {
        var text = Encoding.Unicode.GetString(data, 0, data.Length - (data.Length % 2));
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    private static IEnumerable<string> SplitMultiString(byte[] data)
    {
        var text = Encoding.Unicode.GetString(data, 0, data.Length - (data.Length % 2));
        var parts = text.Split('\0');

        // The data ends with a double NUL, which leaves trailing empty segments.
        var count = parts.Length;
        while (count > 0 && parts[count - 1].Length == 0)
        {
            count--;
        }

        return parts.Take(count);
    }

    internal static string HexPreview(byte[] data)
    {
        if (data.Length == 0)
        {
            return "(zero-length binary value)";
        }

        var shown = Math.Min(data.Length, DisplayHexBytes);
        var text = Convert.ToHexString(data, 0, shown);
        var sb = new StringBuilder(shown * 3);
        for (var i = 0; i < text.Length; i += 2)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(text, i, 2);
        }

        if (data.Length > shown)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" … ({data.Length:N0} bytes)"));
        }

        return sb.ToString();
    }
}
