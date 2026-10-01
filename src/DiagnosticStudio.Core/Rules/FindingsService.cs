using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;

namespace DiagnosticStudio.Core.Rules;

/// <summary>
/// A rule that inspects one parsed artifact. The engine loads each artifact once and offers the document to every
/// rule that applies, so adding a rule does not add another parse of the same file.
/// </summary>
public interface IDocumentRule
{
    string Id { get; }

    /// <summary>Cheap check on artifact metadata; the document is only loaded when at least one rule applies.</summary>
    bool AppliesTo(DiagnosticArtifact artifact);

    /// <summary>Produces findings for the document. Must be deterministic and must not execute any bundle content.</summary>
    IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken);
}

/// <summary>A rule or a document that could not be evaluated. Evaluation of everything else continues.</summary>
public sealed record RuleIssue(string RuleId, string Subject, string Message);

public sealed record FindingsProgress(int ArtifactsEvaluated, int ArtifactsTotal);

/// <param name="Findings">Ordered by severity (most severe first), then title, then id, so output is stable.</param>
public sealed record FindingsResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RuleIssue> Issues,
    int ArtifactsEvaluated)
{
    public static FindingsResult Empty { get; } = new(Array.Empty<Finding>(), Array.Empty<RuleIssue>(), 0);
}

public interface IFindingsService
{
    Task<FindingsResult> EvaluateAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<FindingsProgress>? progress,
        CancellationToken cancellationToken);
}
