using System.Globalization;
using System.Text;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers.Evtx;

/// <summary>Turns a decoded event tree into XML text and into the structured <see cref="EventDetail"/>.</summary>
internal static class EvtxXml
{
    /// <summary>Facts needed to index an event, read without building the XML text.</summary>
    internal readonly record struct SystemFacts(string Provider, uint EventId, byte Level, long? RecordId, bool Complete);

    public static SystemFacts ReadSystemFacts(XmlElem root)
    {
        var system = root.Child("System");
        if (system is null)
        {
            return new SystemFacts(string.Empty, 0, 0, null, false);
        }

        var provider = system.Child("Provider")?.Attribute("Name") ?? string.Empty;
        var eventId = ParseUInt(system.Child("EventID")?.Text) ?? 0;
        var level = (byte)Math.Min(255, ParseUInt(system.Child("Level")?.Text) ?? 0);
        var recordId = long.TryParse(system.Child("EventRecordID")?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;
        return new SystemFacts(provider, eventId, level, recordId, true);
    }

    public static string ToXml(XmlElem root)
    {
        var sb = new StringBuilder(1024);
        Write(sb, root, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, XmlElem element, int depth)
    {
        Indent(sb, depth);
        sb.Append('<').Append(element.Name);
        foreach (var (key, value) in element.Attributes)
        {
            sb.Append(' ').Append(key).Append("=\"");
            AppendEscaped(sb, value, attribute: true);
            sb.Append('"');
        }

        if (element.Content.Count == 0)
        {
            sb.Append(" />\n");
            return;
        }

        var hasElements = element.Content.Any(c => c is XmlElem);
        if (!hasElements)
        {
            sb.Append('>');
            AppendEscaped(sb, element.Text, attribute: false);
            sb.Append("</").Append(element.Name).Append(">\n");
            return;
        }

        sb.Append(">\n");
        foreach (var child in element.Content)
        {
            if (child is XmlElem childElement)
            {
                Write(sb, childElement, depth + 1);
            }
            else if (child is string text && !string.IsNullOrWhiteSpace(text))
            {
                Indent(sb, depth + 1);
                AppendEscaped(sb, text, attribute: false);
                sb.Append('\n');
            }
        }

        Indent(sb, depth);
        sb.Append("</").Append(element.Name).Append(">\n");
    }

    private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

    private static void AppendEscaped(StringBuilder sb, string text, bool attribute)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"' when attribute:
                    sb.Append("&quot;");
                    break;
                case < ' ' when c is not ('\t' or '\n' or '\r'):
                    // Control characters are not representable in XML 1.0; show them rather than dropping data.
                    sb.Append("&#x").Append(((int)c).ToString("X", CultureInfo.InvariantCulture)).Append(';');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    public static EventDetail ToDetail(
        XmlElem root,
        EventSummary summary,
        IEventMessageFormatter? formatter)
    {
        var system = root.Child("System");
        var provider = system?.Child("Provider");
        var execution = system?.Child("Execution");
        var correlation = system?.Child("Correlation");
        var security = system?.Child("Security");

        var providerName = provider?.Attribute("Name") ?? string.Empty;
        var (section, data) = ReadData(root);

        var version = (int)(ParseUInt(system?.Child("Version")?.Text) ?? 0);

        // Only classic events carry a Qualifiers attribute, and they take their message from a message file by id.
        int? classicQualifiers = ParseUInt(system?.Child("EventID")?.Attribute("Qualifiers")) is { } q ? (int)q : null;
        var qualifiers = classicQualifiers ?? 0;
        var message = FormatMessage(providerName, summary.EventId, classicQualifiers, version, data, formatter);

        return new EventDetail
        {
            Summary = summary,
            Provider = providerName,
            ProviderGuid = ParseGuid(provider?.Attribute("Guid")),
            Channel = system?.Child("Channel")?.Text,
            Computer = system?.Child("Computer")?.Text,
            UserSid = security?.Attribute("UserID"),
            Version = version,
            Qualifiers = qualifiers,
            Task = (int)(ParseUInt(system?.Child("Task")?.Text) ?? 0),
            Opcode = (int)(ParseUInt(system?.Child("Opcode")?.Text) ?? 0),
            Keywords = ParseKeywords(system?.Child("Keywords")?.Text),
            ProcessId = (int?)ParseUInt(execution?.Attribute("ProcessID")),
            ThreadId = (int?)ParseUInt(execution?.Attribute("ThreadID")),
            ActivityId = ParseGuid(correlation?.Attribute("ActivityID")),
            RelatedActivityId = ParseGuid(correlation?.Attribute("RelatedActivityID")),
            DataSection = section,
            Data = data,
            Xml = ToXml(root),
            Message = message,
        };
    }

    private static string? FormatMessage(
        string provider,
        uint eventId,
        int? classicQualifiers,
        int version,
        IReadOnlyList<EventDataItem> data,
        IEventMessageFormatter? formatter) =>
        formatter is not null && provider.Length > 0
            ? formatter.Format(provider, eventId, classicQualifiers, version, data)
            : null;

    /// <summary>Text used for in-log filtering: provider plus every data value, and optionally the message.</summary>
    public static string SearchText(XmlElem root, uint eventId, IEventMessageFormatter? messageFormatter)
    {
        var system = root.Child("System");
        var facts = system?.Child("Provider")?.Attribute("Name") ?? string.Empty;
        var (_, data) = ReadData(root);

        var sb = new StringBuilder(facts);
        foreach (var item in data)
        {
            sb.Append('\n');
            if (item.Name is not null)
            {
                sb.Append(item.Name).Append('=');
            }

            sb.Append(item.Value);
        }

        if (messageFormatter is not null)
        {
            var version = (int)(ParseUInt(system?.Child("Version")?.Text) ?? 0);
            int? qualifiers = ParseUInt(system?.Child("EventID")?.Attribute("Qualifiers")) is { } q ? (int)q : null;
            if (FormatMessage(facts, eventId, qualifiers, version, data, messageFormatter) is { } message)
            {
                sb.Append('\n').Append(message);
            }
        }

        return sb.ToString();
    }

    private static (string? Section, IReadOnlyList<EventDataItem> Items) ReadData(XmlElem root)
    {
        foreach (var element in root.Elements)
        {
            if (element.Name == "System")
            {
                continue;
            }

            var items = new List<EventDataItem>();
            if (element.Name == "EventData")
            {
                foreach (var child in element.Elements)
                {
                    items.Add(new EventDataItem(child.Name == "Data" ? child.Attribute("Name") : child.Name, child.Text));
                }
            }
            else
            {
                Flatten(element, items, 0);
            }

            return (element.Name, items);
        }

        return (null, Array.Empty<EventDataItem>());
    }

    // UserData has no fixed schema: list the leaf elements.
    private static void Flatten(XmlElem element, List<EventDataItem> items, int depth)
    {
        var children = element.Elements.ToList();
        if (children.Count == 0 || depth > 8)
        {
            if (depth > 0)
            {
                var text = element.Text;
                if (text.Length == 0 && element.Attributes.Count > 0)
                {
                    text = string.Join(" ", element.Attributes.Where(a => a.Key != "xmlns").Select(a => a.Key + "=" + a.Value));
                }

                items.Add(new EventDataItem(element.Name, text));
            }

            return;
        }

        foreach (var child in children)
        {
            Flatten(child, items, depth + 1);
        }
    }

    private static uint? ParseUInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static ulong ParseKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        text = text.Trim();
        var span = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan();
        return ulong.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static Guid? ParseGuid(string? text) =>
        Guid.TryParse(text, out var guid) && guid != Guid.Empty ? guid : null;
}
