using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Rules.Custom;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Rules;

public sealed class CustomRuleTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DiagnosticStudioTests", Guid.NewGuid().ToString("N"));

    public CustomRuleTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CustomRule Rule(string name = "Crashes", CustomRuleSelector? selector = null) => new()
    {
        Id = "crashes",
        Name = name,
        Selector = selector ?? new CustomRuleSelector { Kind = CustomRuleKind.Event, Provider = "Application Error" },
    };

    private static CustomRule Example() => CustomRuleHelp.Examples.Single(e => e.Name == "Disk problems, by event").Rule;

    private static (DiagnosticArtifact Artifact, FakeLoader Loader) CrashLog(int crashes = 3)
    {
        var artifact = Artifact("Application.evtx", ArtifactType.EventLog);
        var source = new DataEventSource();
        for (var i = 0; i < crashes; i++)
        {
            source.Add("Application Error", 1000, "app.exe");
        }

        source.Add("Other Provider", 1000, "noise");
        source.Add("Application Error", 1001, "noise");
        var loader = new FakeLoader();
        loader.Set(artifact, new DocumentLoadResult(EventLog(artifact, source), null));
        return (artifact, loader);
    }

    private static Task<RuleInput> Input(FakeLoader loader, CustomRule rule, params DiagnosticArtifact[] artifacts) =>
        new RuleInputBuilder(loader).BuildAsync(rule, artifacts, CancellationToken.None);

    // ---- validation ----

    [Fact]
    public void A_rule_needs_a_name()
    {
        var problems = CustomRuleValidator.Validate(Rule(name: " "));

        Assert.Contains(problems, p => p.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_rule_must_say_what_to_look_at_so_it_cannot_match_everything()
    {
        var anyEvent = new CustomRuleSelector { Kind = CustomRuleKind.Event };

        Assert.NotEmpty(CustomRuleValidator.Validate(Rule(selector: anyEvent)));
        Assert.NotEmpty(CustomRuleValidator.Validate(Rule(selector: anyEvent with { NotContains = "x", ExcludeProvider = "y" })));
        Assert.Empty(CustomRuleValidator.Validate(Rule(selector: anyEvent with { Provider = "disk" })));
        Assert.Empty(CustomRuleValidator.Validate(Rule(selector: anyEvent with { After = T0 })));
        Assert.Empty(CustomRuleValidator.Validate(Rule(selector: anyEvent with { Regex = "x" })));
    }

    [Fact]
    public void A_line_rule_must_narrow_what_it_reads()
    {
        var wide = Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line });
        var narrow = Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line, Contains = "error" });

        Assert.NotEmpty(CustomRuleValidator.Validate(wide));
        Assert.Empty(CustomRuleValidator.Validate(narrow));
    }

    [Fact]
    public void A_bad_regex_or_level_is_reported()
    {
        var rule = Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line, Regex = "(", NotRegex = "[", Levels = new[] { "Loud" } });

        var problems = CustomRuleValidator.Validate(rule);

        Assert.Equal(2, problems.Count(p => p.Contains("regular expression", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(problems, p => p.Contains("Loud", StringComparison.Ordinal));
    }

    [Fact]
    public void A_time_range_must_run_forwards()
    {
        var selector = new CustomRuleSelector { Kind = CustomRuleKind.Event, After = T0.AddHours(1), Before = T0 };

        Assert.Contains(CustomRuleValidator.Validate(Rule(selector: selector)), p => p.Contains("start time", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_trigger_settings_are_checked()
    {
        var events = new CustomRuleSelector { Kind = CustomRuleKind.Event, Provider = "disk" };
        var burst = Rule(selector: events) with { Trigger = CustomRuleTrigger.Burst, MinMatches = 1, WindowMinutes = 0 };
        var each = Rule(selector: events) with { Trigger = CustomRuleTrigger.Gap, GroupBy = CustomRuleGrouping.Each };
        var missing = Rule(selector: events) with { Trigger = CustomRuleTrigger.Missing };

        var burstProblems = CustomRuleValidator.Validate(burst);
        Assert.Contains(burstProblems, p => p.Contains("window", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(burstProblems, p => p.Contains("at least 2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CustomRuleValidator.Validate(each), p => p.Contains("one at a time", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(CustomRuleValidator.Validate(missing), p => p.Contains("In files named", StringComparison.Ordinal));
        Assert.Empty(CustomRuleValidator.Validate(missing with { Selector = events with { File = "System.evtx" } }));
    }

    [Fact]
    public void The_report_settings_are_checked()
    {
        var lines = new CustomRuleSelector { Kind = CustomRuleKind.Line, Contains = "x" };

        Assert.Contains(
            CustomRuleValidator.Validate(Rule(selector: lines) with { MinMatches = 0 }),
            p => p.Contains("at least 1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            CustomRuleValidator.Validate(Rule(selector: lines) with { GroupBy = CustomRuleGrouping.Provider }),
            p => p.Contains("event", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            CustomRuleValidator.Validate(Rule(selector: lines) with { GroupBy = CustomRuleGrouping.Capture }),
            p => p.Contains("group", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(CustomRuleValidator.Validate(Rule(selector: lines with { Regex = "code (\\d+)" }) with { GroupBy = CustomRuleGrouping.Capture }));
    }

    [Fact]
    public void Every_example_is_valid()
    {
        Assert.All(CustomRuleHelp.Examples, e => Assert.Empty(CustomRuleValidator.Validate(e.Rule)));
    }

    // ---- JSON and storage ----

    [Fact]
    public void Rules_survive_a_round_trip_through_json_with_every_option()
    {
        var original = new CustomRule
        {
            Id = "all",
            Name = "Everything",
            Description = "d",
            Enabled = false,
            Severity = CustomRuleSeverity.Error,
            Title = "{Name}",
            GroupBy = CustomRuleGrouping.Capture,
            Trigger = CustomRuleTrigger.Burst,
            MinMatches = 4,
            WindowMinutes = 15,
            Selector = new CustomRuleSelector
            {
                Kind = CustomRuleKind.Event,
                File = "*.evtx",
                ExcludeFile = "x*",
                Provider = "disk",
                ExcludeProvider = "y*",
                EventIds = new uint[] { 7, 11 },
                ExcludeEventIds = new uint[] { 9 },
                Levels = new[] { "Error" },
                Contains = "a",
                NotContains = "b",
                Regex = "(?<key>c)",
                NotRegex = "d",
                MatchCase = true,
                DataField = "param1",
                After = T0,
                Before = T0.AddDays(1),
            },
        };

        var json = CustomRuleJson.Serialize(new[] { original });
        Assert.True(CustomRuleJson.TryParse(json, out var rules, out var error), error);

        Assert.Equal(original, Assert.Single(rules), new RuleComparer());
        Assert.Contains("\"Burst\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Capture\"", json, StringComparison.Ordinal);
    }

    /// <summary>Records compare their lists by reference, so compare the saved form instead.</summary>
    private sealed class RuleComparer : IEqualityComparer<CustomRule>
    {
        public bool Equals(CustomRule? x, CustomRule? y) =>
            CustomRuleJson.Serialize(new[] { x! }) == CustomRuleJson.Serialize(new[] { y! });

        public int GetHashCode(CustomRule obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_saved_with_a_script_by_an_older_version_still_loads_and_the_script_is_ignored()
    {
        const string json = """{"schema":1,"rules":[{"id":"old","name":"Old","script":"Remove-Item C:\\","selector":{"kind":"event","provider":"disk"}}]}""";

        Assert.True(CustomRuleJson.TryParse(json, out var rules, out _));

        var rule = Assert.Single(rules);
        Assert.Equal("disk", rule.Selector.Provider);
        Assert.Equal(CustomRuleTrigger.Count, rule.Trigger);
        Assert.Equal(CustomRuleGrouping.All, rule.GroupBy);
        Assert.DoesNotContain("Remove-Item", CustomRuleJson.Serialize(rules), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_and_repeated_ids_are_made_unique()
    {
        var json = """{"schema":1,"rules":[{"name":"A b"},{"id":"same","name":"One"},{"id":"same","name":"Two"}]}""";

        Assert.True(CustomRuleJson.TryParse(json, out var rules, out _));

        Assert.Equal(3, rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Id)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"schema":99,"rules":[]}""")]
    public void Unreadable_or_newer_files_are_refused_with_a_reason(string text)
    {
        Assert.False(CustomRuleJson.TryParse(text, out var rules, out var error));

        Assert.Empty(rules);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_store_starts_empty_and_keeps_what_was_saved()
    {
        var store = new FileCustomRuleStore(Path.Combine(_folder, "rules.json"));

        Assert.Empty(store.Load(out var firstProblem));
        Assert.Null(firstProblem);

        Assert.Null(store.Save(new[] { Example() with { Id = "example" } }));

        var loaded = new FileCustomRuleStore(Path.Combine(_folder, "rules.json")).Load(out var problem);
        Assert.Null(problem);
        Assert.Equal("example", Assert.Single(loaded).Id);
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_and_reported_not_lost()
    {
        var path = Path.Combine(_folder, "rules.json");
        File.WriteAllText(path, "{ broken");
        var store = new FileCustomRuleStore(path);

        var rules = store.Load(out var problem);

        Assert.Empty(rules);
        Assert.NotNull(problem);
        Assert.Equal("{ broken", File.ReadAllText(path + ".bad"));
    }

    // ---- choosing records ----

    [Fact]
    public async Task An_event_selector_keeps_only_matching_events_and_each_ref_points_at_its_record()
    {
        var (artifact, loader) = CrashLog();
        var rule = Rule(selector: new CustomRuleSelector
        {
            Kind = CustomRuleKind.Event,
            File = "application*.evtx",
            Provider = "application error",
            EventIds = new uint[] { 1000 },
        });

        var input = await Input(loader, rule, artifact);

        Assert.Equal(3, input.Count);
        Assert.All(input.Locations.Values, l =>
        {
            Assert.Equal(artifact.Id, l.ArtifactId);
            Assert.Equal(DiagnosticLocationKind.EventRecord, l.Kind);
        });
        Assert.DoesNotContain(input.Records, r => r.Text == "noise");
        Assert.All(input.Records, r => Assert.True(r.IsUtc));
    }

    [Fact]
    public async Task A_file_pattern_that_does_not_match_gives_nothing_and_looks_at_no_file()
    {
        var (artifact, loader) = CrashLog();
        var rule = Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Event, File = "System.evtx" });

        var input = await Input(loader, rule, artifact);

        Assert.Equal(0, input.Count);
        Assert.Empty(input.Files);
    }

    [Fact]
    public async Task Several_patterns_and_exclusions_work_for_files_providers_and_event_ids()
    {
        var (artifact, loader) = CrashLog();
        var events = new CustomRuleSelector { Kind = CustomRuleKind.Event };

        var either = await Input(loader, Rule(selector: events with { Provider = "nothing, Application*" }), artifact);
        var minusOther = await Input(loader, Rule(selector: events with { File = "*.evtx", ExcludeProvider = "other*" }), artifact);
        var minusId = await Input(loader, Rule(selector: events with { File = "*.evtx", ExcludeEventIds = new uint[] { 1000 } }), artifact);
        var minusFile = await Input(loader, Rule(selector: events with { File = "*.evtx", ExcludeFile = "app*" }), artifact);

        Assert.Equal(4, either.Count);
        Assert.Equal(4, minusOther.Count);
        Assert.Equal(1, minusId.Count);
        Assert.Equal(0, minusFile.Count);
    }

    [Fact]
    public async Task A_line_selector_filters_by_file_text_and_regex_and_numbers_lines_from_one()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var other = Artifact("other.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(Text(log, "ok", "ERROR code 12", "fine", "error code 7", "error words"), null));
        loader.Set(other, new DocumentLoadResult(Text(other, "ERROR code 99"), null));
        var rule = Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line, File = "agent.*", Contains = "error", Regex = @"code \d+" });

        var input = await Input(loader, rule, log, other);

        Assert.Equal(2, input.Count);
        Assert.Equal(
            new long[] { 2, 4 },
            input.Locations.Values.Select(l => l.NumericPosition!.Value).Order().ToArray());
        Assert.All(input.Locations.Values, l => Assert.Equal(log.Id, l.ArtifactId));
    }

    [Fact]
    public async Task Text_can_be_required_or_refused_and_case_is_ignored_unless_asked_for()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(Text(log, "ERROR code 12", "error code 7 retrying", "Error words", "fine"), null));
        var lines = new CustomRuleSelector { Kind = CustomRuleKind.Line };

        var anyCase = await Input(loader, Rule(selector: lines with { Contains = "error" }), log);
        var exactCase = await Input(loader, Rule(selector: lines with { Contains = "error", MatchCase = true }), log);
        var without = await Input(loader, Rule(selector: lines with { Contains = "error", NotContains = "RETRYING" }), log);
        var notRegex = await Input(loader, Rule(selector: lines with { Contains = "error", NotRegex = @"code \d+" }), log);
        var regexCase = await Input(loader, Rule(selector: lines with { Regex = @"\bERROR\b", MatchCase = true }), log);

        Assert.Equal(3, anyCase.Count);
        Assert.Equal(1, exactCase.Count);
        Assert.Equal(2, without.Count);
        Assert.Equal(1, notRegex.Count);
        Assert.Equal(1, regexCase.Count);
    }

    [Fact]
    public async Task A_data_item_can_be_looked_at_on_its_own()
    {
        var artifact = Artifact("Security.evtx", ArtifactType.EventLog);
        var source = new DataEventSource();
        source.Add("Auditing", 4625, "alice", "3");
        source.Add("Auditing", 4625, "bob", "2");
        source.Add("Auditing", 4625, "3", "10");
        var loader = new FakeLoader();
        loader.Set(artifact, new DocumentLoadResult(EventLog(artifact, source), null));
        var events = new CustomRuleSelector { Kind = CustomRuleKind.Event, DataField = "param2" };

        var network = await Input(loader, Rule(selector: events with { Regex = "^(3|10)$" }), artifact);
        var missingItem = await Input(loader, Rule(selector: events with { DataField = "param9", Contains = "3" }), artifact);
        var wholeMessage = await Input(loader, Rule(selector: events with { DataField = null, Regex = @"^3 \|" }), artifact);

        Assert.Equal(2, network.Count);
        Assert.Equal(0, missingItem.Count);
        Assert.Equal(1, wholeMessage.Count);
    }

    [Fact]
    public async Task A_time_range_limits_events_and_lines_and_lines_without_a_time_never_match_it()
    {
        var (artifact, loader) = CrashLog();
        var log = Artifact("svc.log", ArtifactType.TextLog);
        loader.Set(log, new DocumentLoadResult(Text(log, "2026-07-23 08:00:00 a", "2026-07-23 09:00:00 b", "no time here", "2026-07-23 10:00:00 c"), null));

        var events = await Input(
            loader,
            Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Event, File = "*.evtx", After = T0.AddMinutes(1), Before = T0.AddMinutes(3) }),
            artifact);
        var lines = await Input(
            loader,
            Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line, File = "*.log", After = new DateTime(2026, 7, 23, 9, 0, 0), Before = new DateTime(2026, 7, 23, 10, 0, 0) }),
            log);

        Assert.Equal(2, events.Count);
        var line = Assert.Single(lines.Records);
        Assert.EndsWith("b", line.Text, StringComparison.Ordinal);
        Assert.False(line.IsUtc);
    }

    [Fact]
    public async Task Each_file_looked_at_is_listed_with_its_matches()
    {
        var one = Artifact("a.log", ArtifactType.TextLog);
        var two = Artifact("b.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(one, new DocumentLoadResult(Text(one, "error", "error"), null));
        loader.Set(two, new DocumentLoadResult(Text(two, "fine"), null));

        var input = await Input(loader, Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line, Contains = "error" }), one, two);

        Assert.Equal(new[] { ("a.log", 2), ("b.log", 0) }, input.Files.Select(f => (f.Name, f.Matches)).ToArray());
        Assert.All(input.Files, f => Assert.Equal(DiagnosticLocationKind.Artifact, f.Location.Kind));
    }

    [Fact]
    public async Task Records_beyond_the_limit_are_counted_but_not_kept()
    {
        var log = Artifact("big.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(Text(log, Enumerable.Repeat("error", RuleInputBuilder.MaxRecords + 25).ToArray()), null));
        var rule = Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Line, Contains = "error" });

        var input = await Input(loader, rule, log);

        Assert.Equal(RuleInputBuilder.MaxRecords, input.Count);
        Assert.Equal(RuleInputBuilder.MaxRecords + 25, input.Matched);
        Assert.True(input.Truncated);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_skipped_not_fatal()
    {
        var (artifact, loader) = CrashLog();
        var broken = Artifact("Broken.evtx", ArtifactType.EventLog);
        loader.Throw(broken, new IOException("locked"));

        var input = await Input(loader, Rule(), broken, artifact);

        Assert.Equal(4, input.Count);
        Assert.Equal("Application.evtx", Assert.Single(input.Files).Name);
    }

    // ---- the engine ----

    [Fact]
    public async Task The_engine_does_not_run_an_invalid_rule_or_one_with_nothing_to_look_at()
    {
        var (artifact, loader) = CrashLog();
        var engine = new CustomRuleEngine(loader);

        var nothing = await engine.RunAsync(
            Rule(selector: new CustomRuleSelector { Kind = CustomRuleKind.Event, EventIds = new uint[] { 424242 } }),
            new[] { artifact },
            CancellationToken.None);
        var invalid = await engine.RunAsync(Rule(name: " "), new[] { artifact }, CancellationToken.None);

        Assert.False(nothing.Ran);
        Assert.Empty(nothing.Problems);
        Assert.False(invalid.Ran);
        Assert.NotEmpty(invalid.Problems);
    }

    // ---- alongside the built-in rules ----

    private sealed class Fixed : IFindingsService
    {
        public Task<FindingsResult> EvaluateAsync(IReadOnlyList<DiagnosticArtifact> artifacts, IProgress<FindingsProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new FindingsResult(
                new[] { new Finding { Id = "b", Severity = FindingSeverity.Information, Title = "Built in", Description = "x" } },
                Array.Empty<RuleIssue>(),
                4));
    }

    private sealed class MemoryStore : ICustomRuleStore
    {
        public MemoryStore(params CustomRule[] rules) => Rules = rules;

        public IReadOnlyList<CustomRule> Rules { get; }

        public IReadOnlyList<CustomRule> Load(out string? problem)
        {
            problem = null;
            return Rules;
        }

        public string? Save(IReadOnlyList<CustomRule> rules) => null;
    }

    private sealed class ScriptedEngine : ICustomRuleEngine
    {
        public List<string> Ran { get; } = new();

        public Task<CustomRuleRun> RunAsync(CustomRule rule, IReadOnlyList<DiagnosticArtifact> artifacts, CancellationToken cancellationToken)
        {
            Ran.Add(rule.Id);
            if (rule.Id == "throws")
            {
                throw new InvalidOperationException("engine fell over");
            }

            var finding = new Finding { Id = "c:" + rule.Id, Severity = FindingSeverity.Error, Title = "Custom " + rule.Id, Description = "x" };
            return Task.FromResult(new CustomRuleRun(new[] { finding }, new[] { "a problem" }, 1, 1, true));
        }
    }

    [Fact]
    public async Task Enabled_custom_rules_add_their_findings_and_problems_to_the_built_in_ones()
    {
        var engine = new ScriptedEngine();
        var store = new MemoryStore(
            Rule() with { Id = "on", Name = "On" },
            Rule() with { Id = "off", Name = "Off", Enabled = false },
            Rule() with { Id = "throws", Name = "Throws" });

        var result = await new CustomRuleFindingsService(new Fixed(), store, engine).EvaluateAsync(Array.Empty<DiagnosticArtifact>(), null, CancellationToken.None);

        Assert.Equal(new[] { "on", "throws" }, engine.Ran);
        Assert.Equal(new[] { "Custom on", "Built in" }, result.Findings.Select(f => f.Title).ToArray());
        Assert.Contains(result.Issues, i => i.Subject == "Custom rule: On" && i.Message == "a problem");
        Assert.Contains(result.Issues, i => i.Subject == "Custom rule: Throws" && i.Message.Contains("engine fell over", StringComparison.Ordinal));
        Assert.Equal(4, result.ArtifactsEvaluated);
    }

    [Fact]
    public async Task With_no_custom_rules_the_built_in_result_is_unchanged()
    {
        var result = await new CustomRuleFindingsService(new Fixed(), new MemoryStore(), new ScriptedEngine())
            .EvaluateAsync(Array.Empty<DiagnosticArtifact>(), null, CancellationToken.None);

        Assert.Equal("Built in", Assert.Single(result.Findings).Title);
        Assert.Empty(result.Issues);
    }
}
