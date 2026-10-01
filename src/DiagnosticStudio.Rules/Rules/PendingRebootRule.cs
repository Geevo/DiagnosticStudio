using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Rules;

namespace DiagnosticStudio.Rules.Rules;

/// <summary>
/// Well-known "restart required" markers present in a registry export. Only the presence of a marker is reported:
/// an export that does not include these keys says nothing about whether a restart is pending.
/// </summary>
public sealed class PendingRebootRule : IDocumentRule
{
    public const string RuleId = "pending-reboot";

    private const string Hklm = @"HKEY_LOCAL_MACHINE\";

    private static readonly Marker[] Markers =
    {
        new("component-servicing", Hklm + @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", null,
            FindingSeverity.Warning, "Component Based Servicing reports a restart is pending",
            "The key RebootPending exists, which servicing writes when an update needs a restart to complete."),
        new("windows-update", Hklm + @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", null,
            FindingSeverity.Warning, "Windows Update reports a restart is required",
            "The key RebootRequired exists, which Windows Update writes when an installed update needs a restart."),
        new("file-rename", Hklm + @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations",
            FindingSeverity.Information, "File renames are queued for the next restart",
            "The value PendingFileRenameOperations is present. Installers use it to replace files in use; it is common and often harmless."),
    };

    public string Id => RuleId;

    public bool AppliesTo(DiagnosticArtifact artifact) => artifact.ArtifactType == ArtifactType.RegistryExport;

    public IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken)
    {
        if (document is not RegistryDocument registry)
        {
            yield break;
        }

        foreach (var marker in Markers)
        {
            var key = registry.FindKey(marker.KeyPath);
            if (key is null || key.IsDeleted)
            {
                continue;
            }

            if (marker.ValueName is not null)
            {
                var value = key.FindValue(marker.ValueName);
                if (value is null || value.IsDeleted)
                {
                    continue;
                }
            }

            yield return new Finding
            {
                Id = RuleSupport.Id(RuleId, artifact.Id, marker.Key),
                Severity = marker.Severity,
                Title = marker.Title,
                Description = marker.Description,
                Evidence = new[]
                {
                    new FindingEvidence
                    {
                        Location = DiagnosticLocation.ForRegistry(artifact.Id, key.FullPath, marker.ValueName),
                        Description = marker.ValueName is null ? key.FullPath : key.FullPath + " · " + marker.ValueName,
                    },
                },
                Tags = new[] { "reboot", "registry" },
                Notes = "Reported from this export only. If the export does not contain these keys, that does not show that no restart is pending.",
            };
        }
    }

    private sealed record Marker(
        string Key,
        string KeyPath,
        string? ValueName,
        FindingSeverity Severity,
        string Title,
        string Description);
}
