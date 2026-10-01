using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Builds event messages from the <em>local machine's</em> registered provider metadata. Two sources are used:
/// manifest providers expose a message template per event id/version, and classic providers expose messages
/// by message id (qualifiers and event id). In both cases the event's data values are substituted for
/// <c>%1</c>, <c>%2</c>, and so on. Providers that are not installed here simply have no message. Nothing is
/// loaded from the diagnostic bundle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ProviderMessageFormatter : IEventMessageFormatter, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ProviderInfo> _providers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string? Format(string provider, uint eventId, int? qualifiers, int version, IReadOnlyList<EventDataItem> data)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return null;
            }

            var info = GetProvider(provider);

            // Classic events (Qualifiers present) resolve their message by id through the provider's message file.
            if (qualifiers is { } q && info.NativeHandle != IntPtr.Zero)
            {
                var messageId = ((uint)q << 16) | (eventId & 0xFFFF);
                if (FormatByMessageId(info.NativeHandle, messageId, data) is { } classic)
                {
                    return classic;
                }
            }

            if (info.Templates is not null && info.Templates.Find(eventId, version) is { } template)
            {
                return Clean(Substitute(template, data));
            }

            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var info in _providers.Values)
            {
                if (info.NativeHandle != IntPtr.Zero)
                {
                    EvtClose(info.NativeHandle);
                }
            }

            _providers.Clear();
        }
    }

    private ProviderInfo GetProvider(string provider)
    {
        if (_providers.TryGetValue(provider, out var cached))
        {
            return cached;
        }

        Templates? templates = null;
        try
        {
            using var metadata = new ProviderMetadata(provider);
            var byKey = new Dictionary<(uint, int), string>();
            foreach (var meta in metadata.Events)
            {
                if (!string.IsNullOrEmpty(meta.Description))
                {
                    byKey[((uint)meta.Id, meta.Version)] = meta.Description;
                }
            }

            templates = new Templates(byKey);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or IOException)
        {
            // Not a registered manifest provider on this machine.
        }

        // Classic providers resolve messages by id through their registered message files.
        var handle = EvtOpenPublisherMetadata(IntPtr.Zero, provider, null, 0, 0);
        var info = new ProviderInfo(templates, handle);
        _providers[provider] = info;
        return info;
    }

    private static string? FormatByMessageId(IntPtr metadata, uint messageId, IReadOnlyList<EventDataItem> data)
    {
        var values = new EvtVariant[data.Count];
        var allocations = new List<IntPtr>(data.Count);
        try
        {
            for (var i = 0; i < data.Count; i++)
            {
                var text = Marshal.StringToHGlobalUni(data[i].Value);
                allocations.Add(text);
                // For string values the variant's Count is the length in characters; zero yields an empty string.
                values[i] = new EvtVariant { StringVal = text, Count = (uint)data[i].Value.Length, Type = EvtVarTypeString };
            }

            const int EvtFormatMessageId = 8;
            EvtFormatMessage(metadata, IntPtr.Zero, messageId, values.Length, values, EvtFormatMessageId, 0, null, out var needed);
            if (needed <= 0 || needed > 1 << 20)
            {
                return null;
            }

            var buffer = new StringBuilder(needed);
            return EvtFormatMessage(metadata, IntPtr.Zero, messageId, values.Length, values, EvtFormatMessageId, needed, buffer, out _)
                ? Clean(buffer.ToString())
                : null;
        }
        finally
        {
            foreach (var pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private static string Substitute(string template, IReadOnlyList<EventDataItem> data) =>
        Placeholder().Replace(template, match =>
        {
            var n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n < 1 || n > data.Count)
            {
                return match.Value;
            }

            var value = data[n - 1].Value;
            return match.Groups[2].Success ? ApplyFormat(value, match.Groups[2].Value) : value;
        });

    // Templates may carry a printf-style modifier, e.g. %2!#x! or %3!08x!. Only integer and string forms are
    // interpreted; anything else (or a value that is not a number) is shown as written.
    private static string ApplyFormat(string value, string format)
    {
        var type = format[^1];
        if (type is 's' or 'S')
        {
            return value;
        }

        if (type is not ('x' or 'X' or 'd' or 'i' or 'u'))
        {
            return value;
        }

        var text = value.Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!(hex
                ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)
                : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
        {
            return value;
        }

        var flags = format[..^1];
        var alternate = flags.Contains('#');
        var digits = flags.Replace("#", string.Empty);
        var zeroPad = digits.StartsWith('0');
        _ = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var width);

        var body = type is 'x' or 'X'
            ? number.ToString(type == 'x' ? "x" : "X", CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);
        body = body.PadLeft(width, zeroPad ? '0' : ' ');
        return alternate && type is 'x' or 'X' ? "0x" + body : body;
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.TrimEnd('\0', '\r', '\n', ' ');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    [GeneratedRegex(@"%(\d{1,2})(?:!([#0-9]*[xXdiu]|[sS])!)?")]
    private static partial Regex Placeholder();

    private sealed record ProviderInfo(Templates? Templates, IntPtr NativeHandle);

    private sealed class Templates
    {
        private readonly Dictionary<(uint Id, int Version), string> _byKey;

        public Templates(Dictionary<(uint, int), string> byKey)
        {
            _byKey = byKey;
        }

        public string? Find(uint id, int version)
        {
            if (_byKey.TryGetValue((id, version), out var exact))
            {
                return exact;
            }

            // Metadata for a different version of the same event is better than nothing, but only if unambiguous.
            var sameId = _byKey.Where(kv => kv.Key.Id == id).Select(kv => kv.Value).Distinct().ToList();
            return sameId.Count == 1 ? sameId[0] : null;
        }
    }

    // ---- wevtapi ----

    private const uint EvtVarTypeString = 1;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct EvtVariant
    {
        [FieldOffset(0)] public IntPtr StringVal;
        [FieldOffset(8)] public uint Count;
        [FieldOffset(12)] public uint Type;
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtOpenPublisherMetadata(
        IntPtr session, string publisherId, string? logFilePath, int locale, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtClose(IntPtr handle);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EvtFormatMessage(
        IntPtr publisherMetadata,
        IntPtr eventHandle,
        uint messageId,
        int valueCount,
        [In] EvtVariant[] values,
        int flags,
        int bufferSize,
        StringBuilder? buffer,
        out int bufferUsed);
}
