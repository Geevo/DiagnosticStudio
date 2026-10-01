using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Rules.Rules;

namespace DiagnosticStudio.Rules;

/// <summary>The built-in proving ruleset. Each rule is independent; the list order does not affect results.</summary>
public static class DefaultRules
{
    public static IReadOnlyList<IDocumentRule> Create() => new IDocumentRule[]
    {
        new ServiceTerminationRule(),
        new ApplicationCrashRule(),
        new PendingRebootRule(),
        new RepeatedLogErrorRule(),
    };
}
