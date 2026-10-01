using System.Globalization;

namespace DiagnosticStudio.Core.Navigation;

/// <summary>
/// Universal reference to source evidence. Everything that points at evidence (search hits,
/// findings, timeline entries, bookmarks) does so through this type.
/// </summary>
/// <remarks>
/// Canonical text form:
/// <code>
/// artifact://{id}
/// artifact://{id}/line/{n}
/// artifact://{id}/event/{recordId}
/// artifact://{id}/registry/{keyPath}[?value={valueName}]
/// artifact://{id}/xml/{path}
/// artifact://{id}/json/{path}
/// </code>
/// </remarks>
public sealed record DiagnosticLocation
{
    public const string Scheme = "artifact";

    public required Guid ArtifactId { get; init; }
    public required DiagnosticLocationKind Kind { get; init; }
    public string? Identifier { get; init; }
    public long? NumericPosition { get; init; }

    /// <summary>
    /// Optional second-level identifier within <see cref="Identifier"/>. For registry locations
    /// <see cref="Identifier"/> is the key path and this is the value name (empty string = the default value);
    /// <c>null</c> means the key itself.
    /// </summary>
    public string? Member { get; init; }

    public static DiagnosticLocation ForArtifact(Guid artifactId) =>
        new() { ArtifactId = artifactId, Kind = DiagnosticLocationKind.Artifact };

    public static DiagnosticLocation ForLine(Guid artifactId, long lineNumber) =>
        new() { ArtifactId = artifactId, Kind = DiagnosticLocationKind.Line, NumericPosition = lineNumber };

    public static DiagnosticLocation ForEventRecord(Guid artifactId, long recordId) =>
        new() { ArtifactId = artifactId, Kind = DiagnosticLocationKind.EventRecord, NumericPosition = recordId };

    /// <param name="keyPath">Full key path, e.g. <c>HKLM\Software\Vendor</c>.</param>
    /// <param name="valueName">Optional value under the key (empty for the default value); <c>null</c> targets the key itself.</param>
    public static DiagnosticLocation ForRegistry(Guid artifactId, string keyPath, string? valueName = null) =>
        new()
        {
            ArtifactId = artifactId,
            Kind = DiagnosticLocationKind.Registry,
            Identifier = keyPath,
            Member = valueName,
        };

    public static DiagnosticLocation ForXmlNode(Guid artifactId, string nodePath) =>
        new() { ArtifactId = artifactId, Kind = DiagnosticLocationKind.XmlNode, Identifier = nodePath };

    public static DiagnosticLocation ForJsonNode(Guid artifactId, string nodePath) =>
        new() { ArtifactId = artifactId, Kind = DiagnosticLocationKind.JsonNode, Identifier = nodePath };

    public override string ToString()
    {
        var id = ArtifactId.ToString("N");
        return Kind switch
        {
            DiagnosticLocationKind.Artifact => $"{Scheme}://{id}",
            DiagnosticLocationKind.Line => $"{Scheme}://{id}/line/{Num()}",
            DiagnosticLocationKind.EventRecord => $"{Scheme}://{id}/event/{Num()}",
            DiagnosticLocationKind.Registry =>
                $"{Scheme}://{id}/registry/{Esc()}" + (Member is null ? string.Empty : "?value=" + Uri.EscapeDataString(Member)),
            DiagnosticLocationKind.XmlNode => $"{Scheme}://{id}/xml/{Esc()}",
            DiagnosticLocationKind.JsonNode => $"{Scheme}://{id}/json/{Esc()}",
            DiagnosticLocationKind.Timeline => $"{Scheme}://{id}/timeline/{Num()}",
            DiagnosticLocationKind.Bookmark => $"{Scheme}://{id}/bookmark/{Esc()}",
            _ => $"{Scheme}://{id}",
        };
    }

    public static bool TryParse(string? text, out DiagnosticLocation? location)
    {
        location = null;
        var prefix = Scheme + "://";
        if (text is null || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text[prefix.Length..].Split('/', 3);
        if (!Guid.TryParseExact(parts[0], "N", out var artifactId))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            location = ForArtifact(artifactId);
            return true;
        }

        var tail = parts.Length == 3 ? parts[2] : string.Empty;
        switch (parts[1])
        {
            case "line" when TryNumber(tail, out var line):
                location = ForLine(artifactId, line);
                return true;
            case "event" when TryNumber(tail, out var record):
                location = ForEventRecord(artifactId, record);
                return true;
            case "timeline" when TryNumber(tail, out var position):
                location = new DiagnosticLocation
                {
                    ArtifactId = artifactId,
                    Kind = DiagnosticLocationKind.Timeline,
                    NumericPosition = position,
                };
                return true;
            case "registry":
                var query = tail.Split('?', 2);
                string? value = null;
                if (query.Length == 2)
                {
                    if (!query[1].StartsWith("value=", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    value = Uri.UnescapeDataString(query[1]["value=".Length..]);
                }

                location = ForRegistry(artifactId, Uri.UnescapeDataString(query[0]), value);
                return true;
            case "xml":
                location = ForXmlNode(artifactId, Uri.UnescapeDataString(tail));
                return true;
            case "json":
                location = ForJsonNode(artifactId, Uri.UnescapeDataString(tail));
                return true;
            case "bookmark":
                location = new DiagnosticLocation
                {
                    ArtifactId = artifactId,
                    Kind = DiagnosticLocationKind.Bookmark,
                    Identifier = Uri.UnescapeDataString(tail),
                };
                return true;
            default:
                return false;
        }
    }

    private string Num() => (NumericPosition ?? 0).ToString(CultureInfo.InvariantCulture);

    private string Esc() => Uri.EscapeDataString(Identifier ?? string.Empty);

    private static bool TryNumber(string s, out long value) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
