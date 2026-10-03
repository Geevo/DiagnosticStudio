using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Rules.Custom;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Rules;

/// <summary>What a rule reports: severity, grouping, titles, and when a group is worth a finding.</summary>
public sealed class PatternRuleTests
{
    private static CustomRuleSelector Events(string? file = null, string? provider = null, string? contains = null, string? regex = null, params uint[] ids) => new()
    {
        Kind = CustomRuleKind.Event,
        File = file,
        Provider = provider,
        Contains = contains,
        Regex = regex,
        EventIds = ids,
    };

    private static CustomRuleSelector Lines(string? file = null, string? contains = null, string? regex = null) => new()
    {
        Kind = CustomRuleKind.Line,
        File = file,
        Contains = contains,
        Regex = regex,
    };

    private static CustomRule Pattern(CustomRuleSelector selector, Action<CustomRuleBuilder>? shape = null)
    {
        var b = new CustomRuleBuilder();
        shape?.Invoke(b);
        return new CustomRule
        {
            Id = "pattern",
            Name = "Pattern",
            Selector = selector,
            Severity = b.Severity,
            Title = b.Title,
            GroupBy = b.GroupBy,
            Trigger = b.Trigger,
            MinMatches = b.MinMatches,
            WindowMinutes = b.WindowMinutes,
        };
    }

    private sealed class CustomRuleBuilder
    {
        public CustomRuleSeverity Severity { get; set; } = CustomRuleSeverity.Warning;
        public string? Title { get; set; }
        public CustomRuleGrouping GroupBy { get; set; } = CustomRuleGrouping.All;
        public CustomRuleTrigger Trigger { get; set; } = CustomRuleTrigger.Count;
        public int MinMatches { get; set; } = 1;
        public int WindowMinutes { get; set; } = 60;
    }

    private static (IReadOnlyList<DiagnosticArtifact> Artifacts, FakeLoader Loader) Everything()
    {
        var application = Artifact("Application.evtx", ArtifactType.EventLog);
        var events = new DataEventSource();
        events.Add("Application Error", 1000, "app.exe");
        events.Add("Application Error", 1000, "app.exe");
        events.Add("Application Error", 1000, "app.exe");
        events.Add("Application Error", 1000, "other.exe");
        events.Add("Disk", 7, "bad block");
        events.Add("Disk", 51, "paging error");

        var agent = Artifact("agent.log", ArtifactType.TextLog);
        var other = Artifact("other.log", ArtifactType.TextLog);
        var service = Artifact("svc.log", ArtifactType.TextLog);

        var loader = new FakeLoader();
        loader.Set(application, new DocumentLoadResult(EventLog(application, events), null));
        loader.Set(
            agent,
            new DocumentLoadResult(Text(agent, "start", "error code 0x80070005", "ok", "ERROR code 0x80070005 again", "error code 0x2", "fine"), null));
        loader.Set(other, new DocumentLoadResult(Text(other, "error code 0x2", "nothing"), null));
        loader.Set(
            service,
            new DocumentLoadResult(
                Text(
                    service,
                    "2026-07-23 08:00:00 started",
                    "2026-07-23 08:30:00 working",
                    "2026-07-23 13:30:00 working",
                    "2026-07-23 13:35:00 stopping"),
                null));
        return (new[] { application, agent, other, service }, loader);
    }

    private static async Task<CustomRuleRun> RunAsync(
        CustomRule rule,
        (IReadOnlyList<DiagnosticArtifact> Artifacts, FakeLoader Loader)? data = null)
    {
        var (artifacts, loader) = data ?? Everything();
        return await new CustomRuleEngine(loader).RunAsync(rule, artifacts, CancellationToken.None);
    }

    // ---- reporting ----

    [Fact]
    public async Task All_matches_become_one_finding_with_the_chosen_severity_and_every_match_as_evidence()
    {
        var rule = Pattern(Events(file: "Application.evtx", provider: "Application Error", ids: 1000), b => b.Severity = CustomRuleSeverity.Error);

        var run = await RunAsync(rule);

        var finding = Assert.Single(run.Findings);
        Assert.Empty(run.Problems);
        Assert.True(run.Ran);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Equal("Pattern (4)", finding.Title);
        Assert.Equal(4, finding.Evidence.Count);
        Assert.All(finding.Evidence, e => Assert.Equal(DiagnosticLocationKind.EventRecord, e.Location.Kind));
        Assert.Contains("custom rule", finding.Tags);
        Assert.Contains("Pattern", finding.Notes, StringComparison.Ordinal);
        Assert.Contains("first at 2026-07-23 14:12:00 UTC, last at 2026-07-23 14:15:00 UTC", finding.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CustomRuleSeverity.Information, FindingSeverity.Information)]
    [InlineData(CustomRuleSeverity.Warning, FindingSeverity.Warning)]
    [InlineData(CustomRuleSeverity.Error, FindingSeverity.Error)]
    public async Task The_rules_severity_is_the_findings_severity(CustomRuleSeverity chosen, FindingSeverity expected)
    {
        var run = await RunAsync(Pattern(Events(provider: "Disk"), b => b.Severity = chosen));

        Assert.Equal(expected, Assert.Single(run.Findings).Severity);
    }

    [Fact]
    public async Task Grouping_by_provider_gives_one_finding_for_each_and_the_minimum_leaves_out_small_groups()
    {
        var all = Pattern(Events(file: "*.evtx"), b => b.GroupBy = CustomRuleGrouping.Provider);
        var big = Pattern(Events(file: "*.evtx"), b =>
        {
            b.GroupBy = CustomRuleGrouping.Provider;
            b.MinMatches = 3;
            b.Title = "{Provider} logged {Count} errors";
        });

        var everyone = await RunAsync(all);
        var onlyBig = await RunAsync(big);

        Assert.Equal(2, everyone.Findings.Count);
        var finding = Assert.Single(onlyBig.Findings);
        Assert.Equal("Application Error logged 4 errors", finding.Title);
    }

    [Fact]
    public async Task Grouping_by_event_id_and_the_title_placeholders_work()
    {
        var rule = Pattern(Events(provider: "Disk"), b =>
        {
            b.GroupBy = CustomRuleGrouping.EventId;
            b.Title = "{Name}: {Provider} event {EventId} x{Count} ({Key}) {Unknown}";
        });

        var run = await RunAsync(rule);

        Assert.Equal(2, run.Findings.Count);
        Assert.Contains(run.Findings, f => f.Title == "Pattern: Disk event 7 x1 (7) {Unknown}");
        Assert.Contains(run.Findings, f => f.Title == "Pattern: Disk event 51 x1 (51) {Unknown}");
    }

    [Fact]
    public async Task The_first_and_last_time_can_be_used_in_a_title()
    {
        var rule = Pattern(Events(provider: "Application Error"), b => b.Title = "{From} to {To} at {Time}");

        var run = await RunAsync(rule);

        Assert.Equal("2026-07-23 14:12:00 UTC to 2026-07-23 14:15:00 UTC at 2026-07-23 14:12:00 UTC", Assert.Single(run.Findings).Title);
    }

    [Fact]
    public async Task One_finding_for_each_match_has_a_stable_distinct_id_and_one_piece_of_evidence()
    {
        var rule = Pattern(Events(provider: "Application Error", ids: 1000), b => b.GroupBy = CustomRuleGrouping.Each);

        var data = Everything();
        var first = await RunAsync(rule, data);
        var second = await RunAsync(rule, data);

        Assert.Equal(4, first.Findings.Count);
        Assert.Equal(4, first.Findings.Select(f => f.Id).Distinct().Count());
        Assert.All(first.Findings, f => Assert.Single(f.Evidence));
        Assert.Equal(first.Findings.Select(f => f.Id), second.Findings.Select(f => f.Id));
        Assert.Contains(first.Findings, f => f.Title.Contains("other.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grouping_text_lines_by_file_counts_each_file_and_links_to_the_lines()
    {
        var rule = Pattern(Lines(contains: "error code"), b =>
        {
            b.GroupBy = CustomRuleGrouping.File;
            b.Title = "{File}: {Count}";
        });

        var run = await RunAsync(rule);

        Assert.Equal(2, run.Findings.Count);
        Assert.Contains(run.Findings, f => f.Title == "agent.log: 3" && f.Evidence.Count == 3);
        Assert.Contains(run.Findings, f => f.Title == "other.log: 1");
        Assert.All(run.Findings.SelectMany(f => f.Evidence), e => Assert.Equal(DiagnosticLocationKind.Line, e.Location.Kind));
    }

    [Fact]
    public async Task Grouping_by_a_captured_value_counts_each_value_and_skips_lines_without_one()
    {
        var rule = Pattern(Lines(regex: @"error code (?<key>0x[0-9A-Fa-f]+)"), b =>
        {
            b.GroupBy = CustomRuleGrouping.Capture;
            b.MinMatches = 2;
            b.Title = "Code {Key} seen {Count} times";
        });

        var run = await RunAsync(rule);

        Assert.Equal(2, run.Findings.Count);
        Assert.Contains(run.Findings, f => f.Title == "Code 0x80070005 seen 2 times");
        Assert.Contains(run.Findings, f => f.Title == "Code 0x2 seen 2 times");
    }

    [Fact]
    public async Task Text_filters_apply_to_what_an_event_says_and_ignore_case()
    {
        var contains = Pattern(Events(contains: "APP.EXE"));
        var regex = Pattern(Events(regex: @"^(bad|paging)\b"), b => b.GroupBy = CustomRuleGrouping.Each);

        var byText = await RunAsync(contains);
        var byRegex = await RunAsync(regex);

        Assert.Equal(3, Assert.Single(byText.Findings).Evidence.Count);
        Assert.Equal(2, byRegex.Findings.Count);
    }

    [Fact]
    public async Task Matches_below_the_minimum_report_nothing_but_the_rule_still_ran()
    {
        var run = await RunAsync(Pattern(Events(provider: "Disk"), b => b.MinMatches = 5));

        Assert.True(run.Ran);
        Assert.Empty(run.Findings);
        Assert.Equal(2, run.RecordsMatched);
    }

    [Fact]
    public async Task Nothing_matching_is_not_an_error()
    {
        var run = await RunAsync(Pattern(Events(provider: "No such provider")));

        Assert.False(run.Ran);
        Assert.Empty(run.Findings);
        Assert.Empty(run.Problems);
    }

    [Fact]
    public async Task An_invalid_rule_is_not_run_and_says_why()
    {
        var run = await RunAsync(Pattern(Events()));

        Assert.False(run.Ran);
        Assert.NotEmpty(run.Problems);
    }

    // ---- when to report ----

    [Fact]
    public async Task A_burst_is_matches_close_together_and_cites_just_those_matches()
    {
        var rule = Pattern(Events(provider: "Application Error"), b =>
        {
            b.Trigger = CustomRuleTrigger.Burst;
            b.MinMatches = 3;
            b.WindowMinutes = 5;
        });

        var data = Everything();
        var run = await RunAsync(rule, data);
        var again = await RunAsync(rule, data);

        var finding = Assert.Single(run.Findings);
        Assert.Equal("Pattern: 4 within 3 min", finding.Title);
        Assert.Equal(4, finding.Evidence.Count);
        Assert.Contains("within 3 minutes, from 2026-07-23 14:12:00 UTC to 2026-07-23 14:15:00 UTC", finding.Description, StringComparison.Ordinal);
        Assert.Equal(finding.Id, Assert.Single(again.Findings).Id);
    }

    [Fact]
    public async Task A_burst_is_reported_once_and_the_search_carries_on_after_it()
    {
        var rule = Pattern(Events(provider: "Application Error"), b =>
        {
            b.Trigger = CustomRuleTrigger.Burst;
            b.MinMatches = 2;
            b.WindowMinutes = 1;
        });

        var run = await RunAsync(rule);

        Assert.Equal(2, run.Findings.Count);
        Assert.All(run.Findings, f => Assert.Equal(2, f.Evidence.Count));
        Assert.Equal(2, run.Findings.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public async Task Matches_that_never_come_close_enough_are_not_a_burst()
    {
        var rule = Pattern(Events(provider: "Application Error"), b =>
        {
            b.Trigger = CustomRuleTrigger.Burst;
            b.MinMatches = 5;
            b.WindowMinutes = 60;
        });

        var run = await RunAsync(rule);

        Assert.True(run.Ran);
        Assert.Empty(run.Findings);
        Assert.Equal(4, run.RecordsMatched);
    }

    [Fact]
    public async Task A_silence_is_a_gap_longer_than_the_window_and_cites_both_sides_of_it()
    {
        var rule = Pattern(Lines(file: "*.log", contains: "2026"), b =>
        {
            b.Trigger = CustomRuleTrigger.Gap;
            b.GroupBy = CustomRuleGrouping.File;
            b.WindowMinutes = 120;
            b.Title = "{File}: nothing for {Minutes} min ({From} to {To})";
        });

        var run = await RunAsync(rule);

        var finding = Assert.Single(run.Findings);
        Assert.Equal("svc.log: nothing for 300 min (2026-07-23 08:30:00 to 2026-07-23 13:30:00)", finding.Title);
        Assert.Contains("No matching record for 300 minutes", finding.Description, StringComparison.Ordinal);
        Assert.Equal(new long[] { 2, 3 }, finding.Evidence.Select(e => e.Location.NumericPosition!.Value).ToArray());
    }

    [Fact]
    public async Task A_gap_that_is_not_longer_than_the_window_is_not_a_silence()
    {
        var rule = Pattern(Events(provider: "Application Error"), b =>
        {
            b.Trigger = CustomRuleTrigger.Gap;
            b.WindowMinutes = 1;
        });

        var run = await RunAsync(rule);

        Assert.True(run.Ran);
        Assert.Empty(run.Findings);
    }

    [Fact]
    public async Task A_file_with_too_few_matches_is_reported_for_the_file_itself()
    {
        var rule = Pattern(Lines(file: "agent.log, other.log", contains: "error code"), b =>
        {
            b.Trigger = CustomRuleTrigger.Missing;
            b.MinMatches = 2;
        });

        var run = await RunAsync(rule);

        var finding = Assert.Single(run.Findings);
        Assert.Equal("Pattern: other.log (1 found)", finding.Title);
        var evidence = Assert.Single(finding.Evidence);
        Assert.Equal(DiagnosticLocationKind.Artifact, evidence.Location.Kind);
        Assert.Contains("expected", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_with_no_match_is_reported_even_when_nothing_matched_anywhere()
    {
        var rule = Pattern(Lines(file: "*.log", contains: "this is nowhere"), b => b.Trigger = CustomRuleTrigger.Missing);

        var run = await RunAsync(rule);

        Assert.True(run.Ran);
        Assert.Equal(0, run.RecordsMatched);
        Assert.Equal(3, run.Findings.Count);
        Assert.Equal(3, run.Findings.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public async Task Nothing_is_reported_as_missing_when_no_file_is_named_like_the_pattern()
    {
        var rule = Pattern(Lines(file: "nothing*.log", contains: "x"), b => b.Trigger = CustomRuleTrigger.Missing);

        var run = await RunAsync(rule);

        Assert.False(run.Ran);
        Assert.Empty(run.Findings);
    }

    // ---- the examples and help shown beside the editor ----

    [Fact]
    public void Every_example_is_a_valid_rule_with_a_description_and_a_unique_name()
    {
        var examples = CustomRuleHelp.Examples;

        Assert.Equal(examples.Count, examples.Select(e => e.Name).Distinct().Count());
        Assert.All(examples, e =>
        {
            Assert.Empty(CustomRuleValidator.Validate(e.Rule));
            Assert.False(string.IsNullOrWhiteSpace(e.Purpose));
        });
    }

    [Fact]
    public void The_help_text_uses_no_banned_words()
    {
        var everything = CustomRuleHelp.HowItWorks + CustomRuleHelp.Tips
            + string.Concat(CustomRuleHelp.Examples.Select(e => e.Name + e.Purpose + e.Settings));
        Assert.DoesNotContain("bund" + "le", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PowerShell", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_examples_run_on_matching_data()
    {
        var system = Artifact("System.evtx", ArtifactType.EventLog);
        var systemEvents = new DataEventSource();
        systemEvents.Add("Microsoft-Windows-Kernel-Power", 41, "x");
        systemEvents.Add("disk", 51, "x");

        var security = Artifact("Security.evtx", ArtifactType.EventLog);
        var securityEvents = new DataEventSource();
        for (var i = 0; i < 5; i++)
        {
            securityEvents.Add("Microsoft-Windows-Security-Auditing", 4625, "3");
        }

        var agent = Artifact("agent1.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(system, new DocumentLoadResult(EventLog(system, systemEvents), null));
        loader.Set(security, new DocumentLoadResult(EventLog(security, securityEvents), null));
        loader.Set(agent, new DocumentLoadResult(Text(agent, "started", "working"), null));
        var engine = new CustomRuleEngine(loader);
        var artifacts = new[] { system, security, agent };

        CustomRule Example(string name) => CustomRuleHelp.Examples.Single(e => e.Name == name).Rule;

        foreach (var name in new[] { "Unexpected shutdown", "Disk problems, by event" })
        {
            var run = await engine.RunAsync(Example(name), artifacts, CancellationToken.None);

            Assert.Empty(run.Problems);
            Assert.NotEmpty(run.Findings);
        }

        // Only the data item name differs: the test data names its items param1, param2...
        var signIns = Example("Many failed sign-ins in a short time");
        var burst = await engine.RunAsync(signIns with { Selector = signIns.Selector with { DataField = "param1" } }, artifacts, CancellationToken.None);
        Assert.Empty(burst.Problems);
        Assert.Single(burst.Findings);

        var missing = await engine.RunAsync(Example("An expected line is missing"), artifacts, CancellationToken.None);
        Assert.Equal("agent1.log has no heartbeat", Assert.Single(missing.Findings).Title);
    }
}
