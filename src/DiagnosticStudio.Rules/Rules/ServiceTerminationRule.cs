using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.Rules.Rules;

/// <summary>
/// Service Control Manager events that say a service stopped when it should not have: 7031 and 7034 (terminated
/// unexpectedly) and 7023 and 7024 (terminated with an error). One finding per service per event log.
/// </summary>
public sealed class ServiceTerminationRule : IDocumentRule
{
    public const string RuleId = "service-termination";

    private const string Provider = "Service Control Manager";
    private const string UnknownService = "(unnamed service)";
    private static readonly IReadOnlySet<uint> EventIds = new HashSet<uint> { 7031, 7034, 7023, 7024 };

    public string Id => RuleId;

    public bool AppliesTo(DiagnosticArtifact artifact) => artifact.ArtifactType == ArtifactType.EventLog;

    public IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken)
    {
        if (document is not EventLogDocument log)
        {
            yield break;
        }

        var matches = RuleSupport.FindEvents(log.Source, Provider, EventIds);
        var groups = new Dictionary<string, List<EventSummary>>(StringComparer.OrdinalIgnoreCase);

        foreach (var summary in matches.Take(RuleSupport.MaxDetailReads))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = log.Source.ReadDetail(summary.Index);
            var name = ServiceName(detail);
            if (!groups.TryGetValue(name, out var list))
            {
                groups[name] = list = new List<EventSummary>();
            }

            list.Add(summary);
        }

        var unexamined = matches.Count - Math.Min(matches.Count, RuleSupport.MaxDetailReads);

        foreach (var (service, events) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = events[0].TimeUtc;
            var last = events[^1].TimeUtc;
            var ids = string.Join(", ", events.Select(e => e.EventId).Distinct().Order());
            var repeated = events.Count > 1;

            var description =
                $"{Provider} event {ids} recorded {RuleSupport.Count(events.Count, "time", "times")} for '{service}'"
                + (repeated ? $" between {RuleSupport.Utc(first)} and {RuleSupport.Utc(last)}." : $" at {RuleSupport.Utc(first)}.");

            yield return new Finding
            {
                Id = RuleSupport.Id(RuleId, artifact.Id, service),
                Severity = repeated ? FindingSeverity.Warning : FindingSeverity.Information,
                Title = repeated
                    ? $"Service '{service}' terminated unexpectedly {events.Count:N0} times"
                    : $"Service '{service}' terminated unexpectedly",
                Description = description,
                Evidence = events.Take(RuleSupport.MaxEvidence).Select(e => RuleSupport.EventEvidence(artifact.Id, e)).ToList(),
                Tags = new[] { "service", "event-log" },
                Notes = Notes(events.Count, unexamined),
            };
        }
    }

    private static string? Notes(int count, int unexamined)
    {
        var notes = new List<string>();
        if (count > RuleSupport.MaxEvidence)
        {
            notes.Add($"The first {RuleSupport.MaxEvidence} of {count:N0} events are listed as evidence.");
        }

        if (unexamined > 0)
        {
            notes.Add($"Only the first {RuleSupport.MaxDetailReads:N0} matching events in the log were attributed to a service; {unexamined:N0} later ones are not counted here.");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // SCM puts the service's display name in the first data item (param1).
    private static string ServiceName(EventDetail detail)
    {
        var item = detail.Data.FirstOrDefault(d => string.Equals(d.Name, "param1", StringComparison.OrdinalIgnoreCase))
                   ?? detail.Data.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Value));
        return string.IsNullOrWhiteSpace(item?.Value) ? UnknownService : item.Value.Trim();
    }
}
