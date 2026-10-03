using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>
/// The built-in rules followed by the user's enabled custom rules. A custom rule that fails, times out or returns
/// something that breaks the contract becomes an issue against that rule; it never stops the others or the built-in ones.
/// </summary>
public sealed class CustomRuleFindingsService : IFindingsService
{
    private readonly IFindingsService _builtIn;
    private readonly ICustomRuleStore _store;
    private readonly ICustomRuleEngine _engine;

    public CustomRuleFindingsService(IFindingsService builtIn, ICustomRuleStore store, ICustomRuleEngine engine)
    {
        _builtIn = builtIn;
        _store = store;
        _engine = engine;
    }

    public async Task<FindingsResult> EvaluateAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<FindingsProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await _builtIn.EvaluateAsync(artifacts, progress, cancellationToken).ConfigureAwait(false);

        var rules = _store.Load(out var loadProblem).Where(r => r.Enabled).ToList();
        if (rules.Count == 0 && loadProblem is null)
        {
            return result;
        }

        var findings = result.Findings.ToList();
        var issues = result.Issues.ToList();
        if (loadProblem is not null)
        {
            issues.Add(new RuleIssue("custom-rules", "Custom rules", loadProblem));
        }

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CustomRuleRun run;
            try
            {
                run = await _engine.RunAsync(rule, artifacts, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                issues.Add(new RuleIssue(rule.Id, "Custom rule: " + rule.Name, "The rule could not be run: " + ex.Message));
                continue;
            }

            findings.AddRange(run.Findings);
            issues.AddRange(run.Problems.Select(p => new RuleIssue(rule.Id, "Custom rule: " + rule.Name, p)));
        }

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
        return new FindingsResult(ordered, issues, result.ArtifactsEvaluated);
    }
}
