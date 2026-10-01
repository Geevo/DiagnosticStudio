using DiagnosticStudio.Core.Artifacts;

namespace DiagnosticStudio.Search;

/// <summary>A parsed global search query: plain text plus optional structured filters.</summary>
public sealed record SearchQuery
{
    /// <summary>Plain-text substring to find. Empty when the query consists only of operators.</summary>
    public string Text { get; init; } = string.Empty;

    public bool MatchCase { get; init; }

    /// <summary>Restricts event logs to these ids (<c>eventid:7031,100-200</c>).</summary>
    public EventIdSet? EventIds { get; init; }

    /// <summary>Restricts event logs to providers whose name contains this text (<c>provider:schannel</c>).</summary>
    public string? ProviderContains { get; init; }

    /// <summary>Restricts event logs to these levels (<c>level:error</c>).</summary>
    public IReadOnlySet<byte>? Levels { get; init; }

    /// <summary>Restricts the search to these artifact types (<c>type:registry</c>).</summary>
    public IReadOnlySet<ArtifactType>? Types { get; init; }

    /// <summary>True when an event-log-only operator is present; other artifact types cannot satisfy it.</summary>
    public bool HasEventOperators => EventIds is not null || ProviderContains is not null || Levels is not null;

    public bool IsEmpty => Text.Length == 0 && !HasEventOperators && Types is null;
}

/// <param name="Errors">Problems with operators in the query; the query should not be run while any exist.</param>
public sealed record SearchQueryParseResult(SearchQuery Query, IReadOnlyList<string> Errors);

/// <summary>
/// Parses text such as <c>0x80072F8F</c>, <c>"print spooler" level:error</c> or <c>eventid:7031 provider:service</c>.
/// Only the operators <c>eventid</c>, <c>provider</c>, <c>level</c> and <c>type</c> are special; anything else,
/// including paths like <c>C:\Windows</c>, is plain text.
/// </summary>
public static class SearchQueryParser
{
    private static readonly Dictionary<string, byte> LevelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = 1,
        ["error"] = 2,
        ["warning"] = 3,
        ["warn"] = 3,
        ["information"] = 4,
        ["informational"] = 4,
        ["info"] = 4,
        ["verbose"] = 5,
    };

    private static readonly Dictionary<string, ArtifactType> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["log"] = ArtifactType.TextLog,
        ["text"] = ArtifactType.TextLog,
        ["cmd"] = ArtifactType.CommandOutput,
        ["command"] = ArtifactType.CommandOutput,
        ["evtx"] = ArtifactType.EventLog,
        ["event"] = ArtifactType.EventLog,
        ["events"] = ArtifactType.EventLog,
        ["eventlog"] = ArtifactType.EventLog,
        ["reg"] = ArtifactType.RegistryExport,
        ["registry"] = ArtifactType.RegistryExport,
        ["xml"] = ArtifactType.Xml,
        ["json"] = ArtifactType.Json,
        ["html"] = ArtifactType.Html,
        ["archive"] = ArtifactType.Archive,
        ["zip"] = ArtifactType.Archive,
        ["cab"] = ArtifactType.Archive,
        ["etl"] = ArtifactType.Trace,
        ["trace"] = ArtifactType.Trace,
        ["binary"] = ArtifactType.Binary,
    };

    public static SearchQueryParseResult Parse(string? input, bool matchCase = false)
    {
        var errors = new List<string>();
        var text = new List<string>();
        EventIdSet? eventIds = null;
        string? provider = null;
        HashSet<byte>? levels = null;
        HashSet<ArtifactType>? types = null;

        foreach (var (token, quoted) in Tokenize(input ?? string.Empty))
        {
            var colon = token.IndexOf(':');
            if (quoted || colon <= 0 || colon == token.Length - 1)
            {
                text.Add(token);
                continue;
            }

            var key = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..];
            switch (key)
            {
                case "eventid":
                    if (EventIdSet.TryParse(value, out var ids, out var idError))
                    {
                        eventIds = ids;
                    }
                    else
                    {
                        errors.Add("eventid: " + idError);
                    }

                    break;

                case "provider":
                    provider = value;
                    break;

                case "level":
                    ParseList(value, "level", LevelNames, ref levels, errors);
                    break;

                case "type":
                    ParseList(value, "type", TypeNames, ref types, errors);
                    break;

                default:
                    text.Add(token); // not one of ours, e.g. "C:\Windows" or "http://host"
                    break;
            }
        }

        var query = new SearchQuery
        {
            Text = string.Join(' ', text),
            MatchCase = matchCase,
            EventIds = eventIds,
            ProviderContains = provider,
            Levels = levels,
            Types = types,
        };
        return new SearchQueryParseResult(query, errors);
    }

    private static void ParseList<T>(
        string value,
        string operatorName,
        Dictionary<string, T> names,
        ref HashSet<T>? target,
        List<string> errors)
        where T : notnull
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (names.TryGetValue(part, out var found))
            {
                (target ??= new HashSet<T>()).Add(found);
            }
            else if (typeof(T) == typeof(byte) && byte.TryParse(part, out var number) && number <= 5)
            {
                (target ??= new HashSet<T>()).Add((T)(object)number);
            }
            else
            {
                errors.Add($"{operatorName}: unknown value '{part}'. Try {string.Join(", ", names.Keys.Take(6))}…");
            }
        }
    }

    private static IEnumerable<(string Token, bool Quoted)> Tokenize(string input)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var startedQuoted = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                if (current.Length == 0 && !inQuotes)
                {
                    startedQuoted = true;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return (current.ToString(), startedQuoted);
                    current.Clear();
                }

                startedQuoted = false;
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return (current.ToString(), startedQuoted);
        }
    }
}
