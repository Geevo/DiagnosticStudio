using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.Rules.Rules;

/// <summary>
/// Application crashes (Application Error, event 1000) and hangs (Application Hang, event 1002) from the
/// Application log. One finding per faulting application per log; the faulting module and exception code are
/// quoted from the events, with no claim about the cause.
/// </summary>
public sealed class ApplicationCrashRule : IDocumentRule
{
    public const string RuleId = "application-crash";

    private static readonly IReadOnlySet<uint> CrashIds = new HashSet<uint> { 1000 };
    private static readonly IReadOnlySet<uint> HangIds = new HashSet<uint> { 1002 };

    public string Id => RuleId;

    public bool AppliesTo(DiagnosticArtifact artifact) => artifact.ArtifactType == ArtifactType.EventLog;

    public IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken)
    {
        if (document is not EventLogDocument log)
        {
            yield break;
        }

        var crashes = RuleSupport.FindEvents(log.Source, "Application Error", CrashIds)
            .Select(s => (Summary: s, Kind: "crash"));
        var hangs = RuleSupport.FindEvents(log.Source, "Application Hang", HangIds)
            .Select(s => (Summary: s, Kind: "hang"));
        var all = crashes.Concat(hangs).OrderBy(e => e.Summary.Index).ToList();

        var groups = new Dictionary<string, List<Occurrence>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (summary, kind) in all.Take(RuleSupport.MaxDetailReads))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = log.Source.ReadDetail(summary.Index);
            var app = Item(detail, 0) ?? "(unknown application)";
            if (!groups.TryGetValue(app, out var list))
            {
                groups[app] = list = new List<Occurrence>();
            }

            // Module and exception code are positional in Application Error (1000) only; Application Hang (1002)
            // carries different data at those positions, so nothing is quoted from it.
            var isCrash = kind == "crash";
            list.Add(new Occurrence(summary, kind, isCrash ? Item(detail, 3) : null, isCrash ? Item(detail, 6) : null));
        }

        foreach (var (app, events) in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var crashCount = events.Count(e => e.Kind == "crash");
            var hangCount = events.Count - crashCount;
            var parts = new List<string>();
            if (crashCount > 0)
            {
                parts.Add(RuleSupport.Count(crashCount, "crash", "crashes"));
            }

            if (hangCount > 0)
            {
                parts.Add(RuleSupport.Count(hangCount, "hang", "hangs"));
            }

            var summaryText = string.Join(" and ", parts);
            var latest = events[^1];
            var description =
                $"{app}: {summaryText} recorded"
                + (events.Count > 1
                    ? $" between {RuleSupport.Utc(events[0].Summary.TimeUtc)} and {RuleSupport.Utc(latest.Summary.TimeUtc)}."
                    : $" at {RuleSupport.Utc(latest.Summary.TimeUtc)}.");
            if (latest.Module is not null || latest.ExceptionCode is not null)
            {
                description += " Most recent event: "
                               + (latest.Module is null ? string.Empty : $"faulting module {latest.Module}")
                               + (latest.Module is not null && latest.ExceptionCode is not null ? ", " : string.Empty)
                               + (latest.ExceptionCode is null ? string.Empty : $"exception code {latest.ExceptionCode}")
                               + ".";
            }

            yield return new Finding
            {
                Id = RuleSupport.Id(RuleId, artifact.Id, app),
                Severity = FindingSeverity.Error,
                Title = $"{app} {(hangCount > 0 && crashCount == 0 ? "stopped responding" : "crashed")}"
                        + (events.Count > 1 ? $" ({events.Count:N0} events)" : string.Empty),
                Description = description,
                Evidence = events.Take(RuleSupport.MaxEvidence)
                    .Select(e => RuleSupport.EventEvidence(artifact.Id, e.Summary, Detail(e)))
                    .ToList(),
                Tags = new[] { "application", "event-log" },
                Notes = events.Count > RuleSupport.MaxEvidence
                    ? $"The first {RuleSupport.MaxEvidence} of {events.Count:N0} events are listed as evidence."
                    : null,
            };
        }
    }

    private static string Detail(Occurrence e)
    {
        var text = e.Kind == "hang" ? "hang" : "crash";
        if (e.Module is not null)
        {
            text += ", module " + e.Module;
        }

        if (e.ExceptionCode is not null)
        {
            text += ", code " + e.ExceptionCode;
        }

        return text;
    }

    // Application Error data is positional: name, version, timestamp, module, module version, timestamp, code, ...
    private static string? Item(EventDetail detail, int position) =>
        position < detail.Data.Count && !string.IsNullOrWhiteSpace(detail.Data[position].Value)
            ? detail.Data[position].Value.Trim()
            : null;

    private sealed record Occurrence(EventSummary Summary, string Kind, string? Module, string? ExceptionCode);
}
