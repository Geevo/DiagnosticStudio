using System.Collections.Concurrent;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.Rules;

/// <summary>
/// Runs the document rules over a bundle. Each artifact is parsed once (up to a few at a time) and handed to every
/// rule that applies to it. A failing rule or an unreadable artifact becomes a <see cref="RuleIssue"/>; it never
/// stops the other rules or artifacts.
/// </summary>
public sealed class FindingsService : IFindingsService
{
    private const int MaxParallelism = 4;

    private readonly IDocumentLoader _loader;
    private readonly IReadOnlyList<IDocumentRule> _rules;
    private readonly IBackgroundWorkGate _gate;

    public FindingsService(IDocumentLoader loader, IEnumerable<IDocumentRule> rules, IBackgroundWorkGate? gate = null)
    {
        _loader = loader;
        _rules = rules.ToList();
        _gate = gate ?? UnlimitedWorkGate.Instance;
    }

    public async Task<FindingsResult> EvaluateAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<FindingsProgress>? progress,
        CancellationToken cancellationToken)
    {
        var findings = new ConcurrentBag<Finding>();
        var issues = new ConcurrentBag<RuleIssue>();
        var candidates = artifacts.Where(a => !a.IsContainer && _rules.Any(r => r.AppliesTo(a))).ToList();
        var done = 0;

        progress?.Report(new FindingsProgress(0, candidates.Count));

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
            async (artifact, token) =>
            {
                using (await _gate.EnterAsync(token).ConfigureAwait(false))
                {
                    await EvaluateArtifactAsync(artifact, findings, issues, token).ConfigureAwait(false);
                }

                progress?.Report(new FindingsProgress(Interlocked.Increment(ref done), candidates.Count));
            }).ConfigureAwait(false);

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
        var orderedIssues = issues
            .OrderBy(i => i.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.RuleId, StringComparer.Ordinal)
            .ToList();

        return new FindingsResult(ordered, orderedIssues, candidates.Count);
    }

    private async Task EvaluateArtifactAsync(
        DiagnosticArtifact artifact,
        ConcurrentBag<Finding> findings,
        ConcurrentBag<RuleIssue> issues,
        CancellationToken token)
    {
        DocumentLoadResult loaded;
        try
        {
            loaded = await _loader.LoadAsync(artifact, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add(new RuleIssue("loader", artifact.ProvenanceDisplay, "Could not be opened for rule evaluation: " + ex.Message));
            return;
        }

        if (loaded.FailureMessage is { } failure)
        {
            issues.Add(new RuleIssue("loader", artifact.ProvenanceDisplay, failure));
        }

        foreach (var rule in _rules.Where(r => r.AppliesTo(artifact)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var finding in rule.Evaluate(artifact, loaded.Document, token))
                {
                    findings.Add(finding);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                issues.Add(new RuleIssue(rule.Id, artifact.ProvenanceDisplay, "Rule failed: " + ex.Message));
            }
        }
    }
}
