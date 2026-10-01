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
        using var enumerator = lines.GetEnumerator();
        var lineNumber = 0;

        while (enumerator.MoveNext())
        {
            lineNumber++;
            if ((lineNumber & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = enumerator.Current;
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

                // hex data can continue onto following lines when a line ends with a backslash.
                if (IsHexValueLine(text))
                {
                    while (text.EndsWith('\\') && enumerator.MoveNext())
                    {
                        lineNumber++;
                        text = text[..^1] + enumerator.Current.Trim();
                    }
                }

                if (!state.TryAddValue(text, startLine))
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

    private static bool IsHeader(ReadOnlySpan<char> text) =>
        text.StartsWith("Windows Registry Editor Version", StringComparison.OrdinalIgnoreCase)
        || text.Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase)
        || text.Equals("REGEDIT5", StringComparison.OrdinalIgnoreCase);

    // Only hex values are continued; a quoted string may legitimately end in an escaped backslash.
    private static bool IsHexValueLine(string line)
    {
        var equals = FindValueSeparator(line, out _);
        if (equals < 0)
        {
            return false;
        }

        return line.AsSpan(equals + 1).TrimStart().StartsWith("hex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the index of the '=' that separates name from data, or -1. <paramref name="name"/> is the unescaped name.</summary>
    private static int FindValueSeparator(string line, out string? name)
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

        public bool TryAddValue(string text, int line)
        {
            var separator = FindValueSeparator(text, out var name);
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
            _current.SetValue(DecodeData(name, isDefault, data, line));
            ValueCount++;
            return true;
        }

        private RegistryValue DecodeData(string name, bool isDefault, string data, int line)
        {
            if (data == "-")
            {
                return RegistryValueDecoder.Deleted(name, isDefault, line);
            }

            if (data.StartsWith('"'))
            {
                return RegistryValueDecoder.String(name, isDefault, UnescapeString(data), line);
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

            return Fail(name, isDefault, data, line, "Unrecognised value data.");
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

    private static string UnescapeString(string data)
    {
        var sb = new StringBuilder(data.Length);
        for (var i = 1; i < data.Length; i++)
        {
            var c = data[i];
            if (c == '\\' && i + 1 < data.Length)
            {
                sb.Append(data[++i]);
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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
