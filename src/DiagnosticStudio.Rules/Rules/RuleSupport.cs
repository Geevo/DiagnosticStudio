using System.Globalization;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Rules.Rules;

internal static class RuleSupport
{
    /// <summary>Evidence rows listed per finding. The finding's text still states the full count.</summary>
    public const int MaxEvidence = 20;

    /// <summary>Events whose detail is read per rule and artifact (to name the service or application).</summary>
    public const int MaxDetailReads = 5000;

    public static string Utc(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    public static string Count(int count, string singular, string plural) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);

    /// <summary>
    /// Summaries of events from <paramref name="provider"/> whose id is in <paramref name="eventIds"/>, in log order.
    /// Only index-level data is touched, so this is cheap even for very large logs.
    /// </summary>
    public static List<EventSummary> FindEvents(IEventLogSource source, string provider, IReadOnlySet<uint> eventIds)
    {
        var providerIds = source.Providers
            .Where(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToHashSet();

        var matches = new List<EventSummary>();
        if (providerIds.Count == 0)
        {
            return matches;
        }

        for (var i = 0; i < source.Count; i++)
        {
            var summary = source.GetSummary(i);
            if (providerIds.Contains(summary.ProviderId) && eventIds.Contains(summary.EventId))
            {
                matches.Add(summary);
            }
        }

        return matches;
    }

    public static FindingEvidence EventEvidence(Guid artifactId, EventSummary summary, string? detail = null) => new()
    {
        Location = DiagnosticLocation.ForEventRecord(artifactId, summary.RecordId),
        Description = $"Event {summary.EventId} · {Utc(summary.TimeUtc)}" + (detail is null ? string.Empty : " · " + detail),
    };

    public static string Id(string rule, Guid artifactId, string key) =>
        $"{rule}:{artifactId:N}:{key.ToLowerInvariant()}";
}
