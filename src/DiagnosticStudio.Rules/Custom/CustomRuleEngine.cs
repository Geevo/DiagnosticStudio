using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Rules.Custom;

/// <summary>The outcome of running one custom rule.</summary>
/// <param name="Findings">The findings the rule produced.</param>
/// <param name="Problems">Everything that went wrong: an invalid rule, or findings dropped for being too many.</param>
/// <param name="RecordsMatched">How many lines or events matched what the rule looks at.</param>
/// <param name="RecordsUsed">How many of those were kept (at most <see cref="RuleInputBuilder.MaxRecords"/>).</param>
/// <param name="Ran">The rule was evaluated; it is not when it is invalid or when it found nothing to evaluate.</param>
public sealed record CustomRuleRun(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Problems,
    int RecordsMatched,
    int RecordsUsed,
    bool Ran);

public interface ICustomRuleEngine
{
    Task<CustomRuleRun> RunAsync(CustomRule rule, IReadOnlyList<DiagnosticArtifact> artifacts, CancellationToken cancellationToken);
}

/// <summary>
/// Gives a rule the records its selector asks for and turns them into findings by the rule's own severity, title,
/// grouping and trigger. Nothing the rule looks at is ever run: records are only read as text.
/// </summary>
public sealed class CustomRuleEngine : ICustomRuleEngine
{
    public const int MaxFindings = 200;
    public const int MaxEvidence = 50;
    public const int MaxTitleLength = 300;

    private readonly RuleInputBuilder _inputs;

    public CustomRuleEngine(IDocumentLoader loader)
    {
        _inputs = new RuleInputBuilder(loader);
    }

    public async Task<CustomRuleRun> RunAsync(CustomRule rule, IReadOnlyList<DiagnosticArtifact> artifacts, CancellationToken cancellationToken)
    {
        var invalid = CustomRuleValidator.Validate(rule);
        if (invalid.Count > 0)
        {
            return new CustomRuleRun(Array.Empty<Finding>(), invalid, 0, 0, false);
        }

        var input = await _inputs.BuildAsync(rule, artifacts, cancellationToken).ConfigureAwait(false);

        // A file with too few matches is a finding even when nothing matched anywhere.
        var missing = rule.Trigger == CustomRuleTrigger.Missing;
        if (input.Files.Count == 0 || (!missing && input.Count == 0))
        {
            return new CustomRuleRun(Array.Empty<Finding>(), Array.Empty<string>(), 0, 0, false);
        }

        var notes = new List<string>();
        if (input.Truncated)
        {
            notes.Add($"{input.Matched:N0} records matched; only the first {input.Count:N0} were used. Narrow the rule.");
        }

        var found = PatternFindings.Build(rule, input, notes);
        return new CustomRuleRun(found, notes, input.Matched, input.Count, true);
    }
}
