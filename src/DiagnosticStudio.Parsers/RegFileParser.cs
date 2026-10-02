using System.Globalization;
using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Parses Windows <c>.reg</c> exports into a <see cref="RegistryDocument"/>. Read-only analysis of text:
/// nothing is ever imported or applied.
/// </summary>
public sealed class RegFileParser : IDiagnosticParser
{
    public const int MaxKeys = 500_000;
    public const int MaxValues = 1_000_000;
    public const int MaxRetainedIssues = 1_000;

    // Hand-written .reg files can have very long hex lines; do not truncate what the parser reads.
    private const int ParseMaxLineChars = 16_000_000;

    // A string value that runs over several lines is read until the next entry; these stop a runaway.
    private const int MaxContinuationLines = 100_000;
    private const int MaxContinuedValueChars = 8_000_000;

    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType == ArtifactType.RegistryExport && artifact.ExtractedPath is not null;

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var path = artifact.ExtractedPath!;
        var raw = await IndexedTextFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var parseSource = await IndexedTextFile.OpenAsync(path, cancellationToken, maxLineChars: ParseMaxLineChars)
            .ConfigureAwait(false);

        return await Task.Run(() => Parse(artifact, parseSource.EnumerateLines(), raw, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Parses already-decoded lines. Exposed for tests and for callers that have text rather than a file.</summary>
    public static RegistryDocument Parse(
        DiagnosticArtifact artifact,
        IEnumerable<string> lines,
        ITextLineSource rawSource,
        CancellationToken cancellationToken = default)
    {
        var state = new ParseState(artifact, rawSource);
        using var cursor = new LineCursor(lines.GetEnumerator());
        var lineNumber = 0;

        while (cursor.MoveNext())
        {
            lineNumber++;
            if ((lineNumber & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = cursor.Current;
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == ';')
            {
                continue;
            }

            if (trimmed[0] == '[')
            {
                if (!state.TryStartKey(trimmed.ToString(), lineNumber))
                {
                    break;
                }

                continue;
            }

            if (trimmed[0] is '"' or '@')
            {
                var startLine = lineNumber;
                var text = trimmed.ToString();

                // regedit escapes quotes and backslashes in strings. Exports without a header come from other tools,
                // which write the text as it is, so there is nothing to unescape.
                var verbatim = state.FormatHeader is null;

                // hex data can continue onto following lines when a line ends with a backslash.
                if (IsHexValueLine(text, verbatim))
                {
                    while (text.EndsWith('\\') && cursor.MoveNext())
                    {
                        lineNumber++;
                        text = text[..^1] + cursor.Current.Trim();
                    }
                }
                else if (IsOpenString(text, verbatim))
                {
                    text = ReadContinuedString(text, cursor, ref lineNumber);
                }

                if (!state.TryAddValue(text, startLine, verbatim))
                {
                    break;
                }

                continue;
            }

            if (state.FormatHeader is null && state.KeyCount == 0 && IsHeader(trimmed))
            {
                state.FormatHeader = trimmed.ToString();
                continue;
            }

            state.AddIssue(lineNumber, "Unrecognised line.");
        }

        return state.Build();
    }

    /// <summary>One line of look-ahead over the lines of the file.</summary>
    private sealed class LineCursor : IDisposable
    {
        private readonly IEnumerator<string> _inner;
        private string? _peeked;

        public LineCursor(IEnumerator<string> inner) => _inner = inner;

        public string Current { get; private set; } = string.Empty;

        public bool MoveNext()
        {
            if (_peeked is not null)
            {
                Current = _peeked;
                _peeked = null;
                return true;
            }

            if (_inner.MoveNext())
            {
                Current = _inner.Current;
                return true;
            }

            return false;
        }

        public bool TryPeek(out string line)
        {
            if (_peeked is null && _inner.MoveNext())
            {
                _peeked = _inner.Current;
            }

            line = _peeked ?? string.Empty;
            return _peeked is not null;
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>A string value whose closing quote is not on the line it starts on.</summary>
    private static bool IsOpenString(string line, bool verbatim)
    {
        var equals = FindValueSeparator(line, verbatim, out _);
        if (equals < 0)
        {
            return false;
        }

        var data = line.AsSpan(equals + 1).Trim();
        return data.Length > 0 && data[0] == '"' && !(data.Length >= 2 && data[^1] == '"');
    }

    /// <summary>
    /// Reads the rest of a string value that runs over several lines (an XML document stored in a value, for
    /// example). The value ends at a line that ends with a quote and is followed by something that starts a new
    /// entry: a blank line, a key header, another value, or the end of the file.
    /// </summary>
    private static string ReadContinuedString(string first, LineCursor cursor, ref int lineNumber)
    {
        var text = new StringBuilder(first);
        var endsWithQuote = false;
        var continued = 0;

        while (cursor.TryPeek(out var next))
        {
            // A blank line ends a value that has closed, but can be part of one that is still open.
            var blank = next.AsSpan().IsWhiteSpace();
            if (StartsEntry(next) && (endsWithQuote || !blank))
            {
                break;
            }

            cursor.MoveNext();
            lineNumber++;
            continued++;
            var part = cursor.Current.TrimEnd();
            text.Append("\r\n").Append(part);
            endsWithQuote = part.Length > 0 && part[^1] == '"';

            if (continued >= MaxContinuationLines || text.Length > MaxContinuedValueChars)
            {
                break;
            }
        }

        return text.ToString().TrimEnd();
    }

    private static bool StartsEntry(string line)
    {
        var text = line.AsSpan().Trim();
        if (text.IsEmpty)
        {
            return true;
        }

        if (text[0] == '[' && text[^1] == ']')
        {
            return true;
        }

        if (text[0] == '@')
        {
            return text.Length > 1 && text[1..].TrimStart().StartsWith("=", StringComparison.Ordinal);
        }

        if (text[0] == '"')
        {
            var close = text[1..].IndexOf('"');
            return close >= 0 && text[(close + 2)..].TrimStart().StartsWith("=", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsHeader(ReadOnlySpan<char> text) =>
        text.StartsWith("Windows Registry Editor Version", StringComparison.OrdinalIgnoreCase)
        || text.Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase)
        || text.Equals("REGEDIT5", StringComparison.OrdinalIgnoreCase);

    // Only hex values are continued; a quoted string may legitimately end in an escaped backslash.
    private static bool IsHexValueLine(string line, bool verbatim)
    {
        var equals = FindValueSeparator(line, verbatim, out _);
        if (equals < 0)
        {
            return false;
        }

        return line.AsSpan(equals + 1).TrimStart().StartsWith("hex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the index of the '=' that separates name from data, or -1. <paramref name="name"/> is the unescaped name.</summary>
    private static int FindValueSeparator(string line, bool verbatim, out string? name)
    {
        name = null;
        if (line.Length == 0)
        {
            return -1;
        }

        int position;
        if (line[0] == '@')
        {
            name = string.Empty;
            position = 1;
        }
        else if (verbatim)
        {
            // The name ends at the first quote that is followed by '='.
            position = 1;
            var end = -1;
            for (var i = 1; i < line.Length; i++)
            {
                if (line[i] != '"')
                {
                    continue;
                }

                var next = i + 1;
                while (next < line.Length && char.IsWhiteSpace(line[next]))
                {
                    next++;
                }

                if (next < line.Length && line[next] == '=')
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                return -1;
            }

            name = line[1..end];
            position = end + 1;
        }
        else
        {
            var sb = new StringBuilder();
            position = 1;
            var closed = false;
            while (position < line.Length)
            {
                var c = line[position++];
                if (c == '\\' && position < line.Length)
                {
                    sb.Append(line[position++]);
                }
                else if (c == '"')
                {
                    closed = true;
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (!closed)
            {
                return -1;
            }

            name = sb.ToString();
        }

        while (position < line.Length && char.IsWhiteSpace(line[position]))
        {
            position++;
        }

        return position < line.Length && line[position] == '=' ? position : -1;
    }

    private sealed class ParseState
    {
        private readonly DiagnosticArtifact _artifact;
        private readonly ITextLineSource _raw;
        private readonly RegistryKey _root = new(string.Empty, null, 0);
        private readonly List<RegistryKey> _keys = new();
        private readonly HashSet<RegistryKey> _listed = new();
        private readonly List<RegistryParseIssue> _issues = new();
        private RegistryKey? _current;
        private int _lineAtStop;

        public ParseState(DiagnosticArtifact artifact, ITextLineSource raw)
        {
            _artifact = artifact;
            _raw = raw;
        }

        public string? FormatHeader { get; set; }
        public int KeyCount { get; private set; }
        public int ValueCount { get; private set; }
        public int TotalIssues { get; private set; }
        public bool Truncated { get; private set; }

        public void AddIssue(int line, string message)
        {
            TotalIssues++;
            if (_issues.Count < MaxRetainedIssues)
            {
                _issues.Add(new RegistryParseIssue(line, message));
            }
        }

        /// <summary>Returns <c>false</c> when parsing must stop because a size limit was hit.</summary>
        public bool TryStartKey(string headerLine, int line)
        {
            var close = headerLine.LastIndexOf(']');
            if (close < 0)
            {
                _current = null;
                AddIssue(line, "Key header is missing its closing ']'.");
                return true;
            }

            var path = headerLine[1..close];
            var deleted = path.StartsWith('-');
            if (deleted)
            {
                path = path[1..];
            }

            var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                _current = null;
                AddIssue(line, "Key header has an empty path.");
                return true;
            }

            var key = _root;
            foreach (var segment in segments)
            {
                key = key.GetOrAddChild(segment, line, out var added);
                if (added)
                {
                    List(key);
                    KeyCount++;
                    if (KeyCount > MaxKeys)
                    {
                        Stop(line);
                        return false;
                    }
                }
            }

            key.HasHeader = true;
            key.IsDeleted = deleted;
            _current = key;
            return true;
        }

        public bool TryAddValue(string text, int line, bool verbatim)
        {
            var separator = FindValueSeparator(text, verbatim, out var name);
            if (separator < 0 || name is null)
            {
                AddIssue(line, "Value line has no valid name and '='.");
                return true;
            }

            if (_current is null)
            {
                AddIssue(line, "Value appears before any key header.");
                return true;
            }

            if (ValueCount >= MaxValues)
            {
                Stop(line);
                return false;
            }

            var isDefault = text[0] == '@';
            var data = text[(separator + 1)..].Trim();
            _current.SetValue(DecodeData(name, isDefault, data, line, verbatim));
            ValueCount++;
            return true;
        }

        private RegistryValue DecodeData(string name, bool isDefault, string data, int line, bool verbatim)
        {
            if (data == "-")
            {
                return RegistryValueDecoder.Deleted(name, isDefault, line);
            }

            if (data.StartsWith('"'))
            {
                return RegistryValueDecoder.String(name, isDefault, StringValue(data, verbatim), line);
            }

            if (TryPrefixed(data, "dword:", out var dwordText))
            {
                if (uint.TryParse(dwordText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var dword))
                {
                    return RegistryValueDecoder.DWord(name, isDefault, dword, line);
                }

                return Fail(name, isDefault, data, line, "Invalid dword value.");
            }

            if (TryPrefixed(data, "qword:", out var qwordText))
            {
                if (ulong.TryParse(qwordText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var qword))
                {
                    return RegistryValueDecoder.QWord(name, isDefault, qword, line);
                }

                return Fail(name, isDefault, data, line, "Invalid qword value.");
            }

            if (data.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeHex(name, isDefault, data, line);
            }

            if (TryToolTyped(name, isDefault, data, line, verbatim) is { } typed)
            {
                return typed;
            }

            return Fail(name, isDefault, data, line, "Unrecognised value data.");
        }

        // Exports from other tools write the type as a word before the value: MULTI_SZ:"text", REG_BINARY:0A,0B.
        private RegistryValue? TryToolTyped(string name, bool isDefault, string data, int line, bool verbatim)
        {
            var colon = data.IndexOf(':');
            if (colon <= 0 || colon > 20)
            {
                return null;
            }

            var token = data[..colon].Trim().ToUpperInvariant();
            if (token.StartsWith("REG_", StringComparison.Ordinal))
            {
                token = token[4..];
            }

            var rest = data[(colon + 1)..].Trim();
            switch (token)
            {
                case "SZ":
                case "EXPAND_SZ":
                case "MULTI_SZ":
                case "LINK":
                    var text = rest.StartsWith('"') ? StringValue(rest, verbatim) : rest;
                    return token switch
                    {
                        "EXPAND_SZ" => RegistryValueDecoder.Text(name, isDefault, RegistryValueKind.ExpandString, "REG_EXPAND_SZ", text, line),
                        "MULTI_SZ" => RegistryValueDecoder.Text(name, isDefault, RegistryValueKind.MultiString, "REG_MULTI_SZ", text, line),
                        "LINK" => RegistryValueDecoder.Text(name, isDefault, RegistryValueKind.Link, "REG_LINK", text, line),
                        _ => RegistryValueDecoder.String(name, isDefault, text, line),
                    };

                case "BINARY":
                case "NONE":
                    if (rest.Length == 0)
                    {
                        return RegistryValueDecoder.FromBytes(name, isDefault, token == "NONE" ? 0 : 3, Array.Empty<byte>(), line);
                    }

                    return TryParseBytes(rest, out var bytes)
                        ? RegistryValueDecoder.FromBytes(name, isDefault, token == "NONE" ? 0 : 3, bytes, line)
                        : Fail(name, isDefault, data, line, "Hex data contains an invalid byte.");

                default:
                    return null;
            }
        }

        private RegistryValue DecodeHex(string name, bool isDefault, string data, int line)
        {
            var colon = data.IndexOf(':');
            if (colon < 0)
            {
                return Fail(name, isDefault, data, line, "Hex value is missing ':'.");
            }

            var type = 3;
            var typeSpec = data[3..colon].Trim();
            if (typeSpec.Length > 0)
            {
                if (typeSpec[0] != '(' || typeSpec[^1] != ')'
                    || !int.TryParse(typeSpec[1..^1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out type))
                {
                    return Fail(name, isDefault, data, line, "Invalid hex type specifier.");
                }
            }

            var body = data[(colon + 1)..].Trim();
            if (!TryParseBytes(body, out var bytes))
            {
                return Fail(name, isDefault, data, line, "Hex data contains an invalid byte.");
            }

            return RegistryValueDecoder.FromBytes(name, isDefault, type, bytes, line);
        }

        private RegistryValue Fail(string name, bool isDefault, string data, int line, string message)
        {
            AddIssue(line, message);
            return RegistryValueDecoder.Undecodable(name, isDefault, data, line);
        }

        private void List(RegistryKey key)
        {
            if (_listed.Add(key))
            {
                _keys.Add(key);
            }
        }

        private void Stop(int line)
        {
            Truncated = true;
            _lineAtStop = line;
        }

        public RegistryDocument Build() => new()
        {
            Artifact = _artifact,
            Root = _root,
            Keys = _keys,
            KeyCount = KeyCount,
            ValueCount = ValueCount,
            FormatHeader = FormatHeader,
            Issues = _issues,
            TotalIssueCount = TotalIssues,
            IsTruncated = Truncated,
            TruncatedAtLine = _lineAtStop,
            RawSource = _raw,
        };
    }

    private static bool TryPrefixed(string data, string prefix, out string rest)
    {
        if (data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = data[prefix.Length..].Trim();
            return true;
        }

        rest = string.Empty;
        return false;
    }

    /// <summary>
    /// The text of a quoted string value. The value ends at the last quote on its (logical) line, so quotes inside
    /// it do not cut it short. Strict exports escape <c>\\</c> and <c>\"</c>; a value that contains an unescaped
    /// quote cannot be strict, so it (and every value of a header-less file) is taken as written.
    /// </summary>
    private static string StringValue(string data, bool verbatim)
    {
        var end = data.LastIndexOf('"');
        var inner = end > 0 ? data[1..end] : data[1..];

        if (verbatim || HasUnescapedQuote(inner))
        {
            return inner;
        }

        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                sb.Append(inner[++i]);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool HasUnescapedQuote(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '"')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBytes(string text, out byte[] bytes)
    {
        if (text.Length == 0)
        {
            bytes = Array.Empty<byte>();
            return true;
        }

        var tokens = text.Split(',');
        bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim();
            if (token.Length is 0 or > 2
                || !byte.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
            {
                // A trailing comma before a continuation is tolerated.
                if (token.Length == 0 && i == tokens.Length - 1)
                {
                    bytes = bytes[..i];
                    return true;
                }

                return false;
            }
        }

        return true;
    }
}
