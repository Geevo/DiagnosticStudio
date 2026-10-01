using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Rules.Rules;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Rules;

public class RuleTests
{
    private static List<Finding> Run(IDocumentRule rule, DiagnosticArtifact artifact, DiagnosticDocument document) =>
        rule.Evaluate(artifact, document, CancellationToken.None).ToList();

    // ---- service termination ----

    [Fact]
    public void Repeated_service_termination_is_one_warning_citing_each_event()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var source = new DataEventSource()
            .Add("Service Control Manager", 7036, "Print Spooler", "running")
            .Add("Service Control Manager", 7031, "Agent Service", "1", "60000")
            .Add("Service Control Manager", 7031, "Agent Service", "2", "60000")
            .Add("Service Control Manager", 7034, "Agent Service", "3")
            .Add("Service Control Manager", 7031, "Print Spooler", "1", "60000");

        var findings = Run(new ServiceTerminationRule(), artifact, EventLog(artifact, source));

        Assert.Equal(2, findings.Count);
        var agent = Assert.Single(findings, f => f.Title.Contains("Agent Service"));
        Assert.Equal(FindingSeverity.Warning, agent.Severity);
        Assert.Equal("Service 'Agent Service' terminated unexpectedly 3 times", agent.Title);
        Assert.Contains("7031, 7034", agent.Description);
        Assert.Contains("2026-07-23 14:13:00 UTC", agent.Description);
        Assert.Contains("2026-07-23 14:15:00 UTC", agent.Description);
        Assert.Equal(new long[] { 1001, 1002, 1003 }, agent.Evidence.Select(e => e.Location.NumericPosition!.Value));
        Assert.All(agent.Evidence, e =>
        {
            Assert.Equal(artifact.Id, e.Location.ArtifactId);
            Assert.Equal(DiagnosticLocationKind.EventRecord, e.Location.Kind);
        });

        // The one-off is reported at a lower severity, not hidden.
        var spooler = Assert.Single(findings, f => f.Title.Contains("Print Spooler"));
        Assert.Equal(FindingSeverity.Information, spooler.Severity);
    }

    [Fact]
    public void Service_names_group_case_insensitively_and_other_providers_are_ignored()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var source = new DataEventSource()
            .Add("Service Control Manager", 7031, "agent service")
            .Add("Service Control Manager", 7031, "Agent Service")
            .Add("Some Other Provider", 7031, "Agent Service")
            .Add("Service Control Manager", 7036, "Agent Service");

        var finding = Assert.Single(Run(new ServiceTerminationRule(), artifact, EventLog(artifact, source)));

        Assert.Contains("2 times", finding.Title);
    }

    [Fact]
    public void Evidence_is_capped_but_the_count_is_not()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var source = new DataEventSource();
        for (var i = 0; i < 45; i++)
        {
            source.Add("Service Control Manager", 7031, "Agent Service");
        }

        var finding = Assert.Single(Run(new ServiceTerminationRule(), artifact, EventLog(artifact, source)));

        Assert.Contains("45 times", finding.Title);
        Assert.Equal(RuleSupport.MaxEvidence, finding.Evidence.Count);
        Assert.Contains("first 20 of 45", finding.Notes);
    }

    [Fact]
    public void No_matching_events_means_no_findings()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var source = new DataEventSource().Add("Service Control Manager", 7036, "Agent Service", "running");

        Assert.Empty(Run(new ServiceTerminationRule(), artifact, EventLog(artifact, source)));
    }

    // ---- application crash ----

    [Fact]
    public void Application_crashes_group_by_application_and_quote_the_faulting_module()
    {
        var artifact = Artifact("Application.evtx", ArtifactType.EventLog);
        var source = new DataEventSource()
            .Add("Application Error", 1000, "Agent.exe", "1.0.0.0", "5f1", "ntdll.dll", "10.0", "5f2", "c0000005", "0001")
            .Add("Application Error", 1000, "Agent.exe", "1.0.0.0", "5f1", "KERNELBASE.dll", "10.0", "5f2", "c0000409", "0002")
            .Add("Application Hang", 1002, "Other.exe", "1.0", "1a2b", "datetime", "datetime", "Other.exe", "not-a-module", "not-a-code");

        var findings = Run(new ApplicationCrashRule(), artifact, EventLog(artifact, source));

        var crash = Assert.Single(findings, f => f.Title.StartsWith("Agent.exe"));
        Assert.Equal(FindingSeverity.Error, crash.Severity);
        Assert.Equal("Agent.exe crashed (2 events)", crash.Title);
        Assert.Contains("faulting module KERNELBASE.dll, exception code c0000409", crash.Description);
        Assert.Equal(2, crash.Evidence.Count);
        Assert.Contains("module ntdll.dll", crash.Evidence[0].Description);

        var hang = Assert.Single(findings, f => f.Title.StartsWith("Other.exe"));
        Assert.Equal("Other.exe stopped responding", hang.Title);

        // A hang's data is not laid out like a crash's, so nothing is quoted as module or exception code.
        Assert.DoesNotContain("faulting module", hang.Description);
        Assert.DoesNotContain("not-a-code", hang.Description);
        Assert.DoesNotContain("not-a-code", hang.Evidence[0].Description);
    }

    // ---- pending reboot ----

    private static RegistryDocument Registry(DiagnosticArtifact artifact, params (string Key, string? Value)[] entries)
    {
        var root = new RegistryKey(string.Empty, null, 0);
        var keys = new List<RegistryKey>();
        foreach (var (path, value) in entries)
        {
            var key = root;
            foreach (var segment in path.Split('\\'))
            {
                key = key.GetOrAddChild(segment, 1, out _);
            }

            key.HasHeader = true;
            if (value is not null)
            {
                key.SetValue(new RegistryValue { Name = value, Kind = RegistryValueKind.MultiString, TypeName = "REG_MULTI_SZ", DisplayValue = "x" });
            }

            keys.Add(key);
        }

        return new RegistryDocument
        {
            Artifact = artifact,
            Root = root,
            Keys = keys,
            RawSource = new ListLines(Array.Empty<string>()),
        };
    }

    [Fact]
    public void Pending_reboot_markers_are_reported_with_registry_evidence()
    {
        var artifact = Artifact("system.reg", ArtifactType.RegistryExport);
        var doc = Registry(
            artifact,
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", null),
            (@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"));

        var findings = Run(new PendingRebootRule(), artifact, doc);

        Assert.Equal(2, findings.Count);
        var servicing = Assert.Single(findings, f => f.Title.Contains("Component Based Servicing"));
        Assert.Equal(FindingSeverity.Warning, servicing.Severity);
        var evidence = Assert.Single(servicing.Evidence).Location;
        Assert.Equal(DiagnosticLocationKind.Registry, evidence.Kind);
        Assert.EndsWith(@"Component Based Servicing\RebootPending", evidence.Identifier);
        Assert.Null(evidence.Member);

        var rename = Assert.Single(findings, f => f.Title.Contains("File renames"));
        Assert.Equal(FindingSeverity.Information, rename.Severity);
        Assert.Equal("PendingFileRenameOperations", Assert.Single(rename.Evidence).Location.Member);
    }

    [Fact]
    public void A_session_manager_key_without_the_value_is_not_a_finding()
    {
        var artifact = Artifact("system.reg", ArtifactType.RegistryExport);
        var doc = Registry(artifact, (@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", null));

        Assert.Empty(Run(new PendingRebootRule(), artifact, doc));
    }

    // ---- repeated log errors ----

    [Fact]
    public void Repeated_error_lines_are_grouped_once_numbers_and_times_are_ignored()
    {
        var artifact = Artifact("agent.log", ArtifactType.TextLog);
        var doc = Text(
            artifact,
            "2026-07-23 14:12:00 INFO started",
            "2026-07-23 14:12:01 ERROR connect to 10.0.0.5:443 failed after 3 retries",
            "2026-07-23 14:12:02 WARN slow",
            "2026-07-23 14:13:10 ERROR connect to 10.0.0.9:443 failed after 5 retries",
            "2026-07-23 14:14:20 ERROR connect to 10.0.0.7:8443 failed after 7 retries",
            "2026-07-23 14:15:00 ERROR something else entirely");

        var finding = Assert.Single(Run(new RepeatedLogErrorRule(), artifact, doc));

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("3 occurrences", finding.Title);
        Assert.Contains("first at line 2", finding.Description);
        Assert.Contains("14:12:01", finding.Description);
        Assert.Contains("14:14:20", finding.Description);
        Assert.Equal(new long[] { 2, 4, 5 }, finding.Evidence.Select(e => e.Location.NumericPosition!.Value));
        Assert.All(finding.Evidence, e => Assert.Equal(DiagnosticLocationKind.Line, e.Location.Kind));
    }

    [Fact]
    public void Two_occurrences_are_not_yet_repeated()
    {
        var artifact = Artifact("agent.log", ArtifactType.TextLog);
        var doc = Text(artifact, "2026-07-23 14:12:01 ERROR boom", "2026-07-23 14:12:02 ERROR boom");

        Assert.Empty(Run(new RepeatedLogErrorRule(), artifact, doc));
    }

    [Fact]
    public void Words_that_merely_contain_error_are_not_errors()
    {
        var artifact = Artifact("agent.log", ArtifactType.TextLog);
        var doc = Text(artifact, Enumerable.Repeat("2026-07-23 14:12:01 INFO errors=0 terror handler ready", 5).ToArray());

        Assert.Empty(Run(new RepeatedLogErrorRule(), artifact, doc));
    }

    [Fact]
    public void Cmtrace_error_entries_count()
    {
        var artifact = Artifact("cm.log", ArtifactType.TextLog);
        var line = "<![LOG[Failed to download {0}]LOG]!><time=\"14:12:00.123+000\" date=\"7-23-2026\" component=\"X\" context=\"\" type=\"3\" thread=\"1\" file=\"\">";
        var doc = Text(artifact, line, line, line);

        var finding = Assert.Single(Run(new RepeatedLogErrorRule(), artifact, doc));

        Assert.Contains("3 occurrences", finding.Title);
    }

    [Fact]
    public void Only_the_most_frequent_patterns_are_listed_and_the_rest_are_summarised()
    {
        var artifact = Artifact("agent.log", ArtifactType.TextLog);
        var lines = new List<string>();
        for (var pattern = 0; pattern < 12; pattern++)
        {
            for (var repeat = 0; repeat < 3 + pattern; repeat++)
            {
                lines.Add($"2026-07-23 14:12:01 ERROR pattern-{(char)('a' + pattern)} failed");
            }
        }

        var findings = Run(new RepeatedLogErrorRule(), artifact, Text(artifact, lines.ToArray()));

        Assert.Equal(11, findings.Count);
        Assert.Equal(10, findings.Count(f => f.Severity == FindingSeverity.Warning));
        var more = Assert.Single(findings, f => f.Severity == FindingSeverity.Information);
        Assert.Contains("2 more repeated error patterns", more.Title);
        // Most frequent pattern first.
        Assert.Contains("pattern-l", findings[0].Description);
    }

    // ---- applicability and wrong document types ----

    [Fact]
    public void Rules_only_apply_to_their_artifact_types()
    {
        Assert.True(new ServiceTerminationRule().AppliesTo(Artifact("a", ArtifactType.EventLog)));
        Assert.False(new ServiceTerminationRule().AppliesTo(Artifact("a", ArtifactType.TextLog)));
        Assert.True(new PendingRebootRule().AppliesTo(Artifact("a", ArtifactType.RegistryExport)));
        Assert.False(new PendingRebootRule().AppliesTo(Artifact("a", ArtifactType.EventLog)));
        Assert.True(new RepeatedLogErrorRule().AppliesTo(Artifact("a", ArtifactType.TextLog)));
        Assert.False(new RepeatedLogErrorRule().AppliesTo(Artifact("a", ArtifactType.Binary)));
    }

    [Fact]
    public void A_rule_given_an_unparsed_document_quietly_finds_nothing()
    {
        var artifact = Artifact("System.evtx", ArtifactType.EventLog);
        var unsupported = new UnsupportedDocument { Artifact = artifact, Reason = "not parsed" };

        foreach (var rule in DefaultRules.Create())
        {
            Assert.Empty(Run(rule, artifact, unsupported));
        }
    }
}
