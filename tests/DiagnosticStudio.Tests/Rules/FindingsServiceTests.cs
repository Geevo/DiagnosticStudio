using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Rules;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Rules;

public class FindingsServiceTests
{
    private sealed class ProbeRule : IDocumentRule
    {
        private readonly Func<DiagnosticArtifact, bool> _applies;
        private readonly Func<DiagnosticArtifact, IEnumerable<Finding>> _evaluate;

        public ProbeRule(string id, Func<DiagnosticArtifact, bool> applies, Func<DiagnosticArtifact, IEnumerable<Finding>> evaluate)
        {
            Id = id;
            _applies = applies;
            _evaluate = evaluate;
        }

        public string Id { get; }
        public bool AppliesTo(DiagnosticArtifact artifact) => _applies(artifact);
        public IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken) => _evaluate(artifact);
    }

    private static Finding F(string id, FindingSeverity severity, string title) => new()
    {
        Id = id,
        Severity = severity,
        Title = title,
        Description = title,
    };

    private static DocumentLoadResult Doc(DiagnosticArtifact artifact, string? failure = null) =>
        new(new UnsupportedDocument { Artifact = artifact, Reason = "test" }, failure);

    [Fact]
    public async Task Each_artifact_is_loaded_once_however_many_rules_apply()
    {
        var artifact = Artifact("a.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Doc(artifact));
        var rules = new[]
        {
            new ProbeRule("r1", _ => true, a => new[] { F("1", FindingSeverity.Information, "one") }),
            new ProbeRule("r2", _ => true, a => new[] { F("2", FindingSeverity.Information, "two") }),
        };

        var result = await new FindingsService(loader, rules).EvaluateAsync(new[] { artifact }, null, CancellationToken.None);

        Assert.Single(loader.Loaded);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public async Task Artifacts_no_rule_applies_to_are_never_opened()
    {
        var wanted = Artifact("a.log", ArtifactType.TextLog);
        var other = Artifact("b.bin", ArtifactType.Binary);
        var archive = Artifact("c.zip", ArtifactType.Archive) with { IsContainer = true };
        var loader = new FakeLoader();
        loader.Set(wanted, Doc(wanted));
        var rule = new ProbeRule("r", a => true, a => Array.Empty<Finding>());

        var result = await new FindingsService(loader, new[] { rule })
            .EvaluateAsync(new[] { wanted, other, archive }, null, CancellationToken.None);

        // The rule says it applies to everything, but opened containers are never evaluated.
        Assert.Equal(2, result.ArtifactsEvaluated);
        Assert.DoesNotContain(archive.Id, loader.Loaded);
    }

    [Fact]
    public async Task Findings_are_ordered_by_severity_then_title_regardless_of_completion_order()
    {
        var artifacts = Enumerable.Range(0, 8).Select(i => Artifact($"f{i}.log", ArtifactType.TextLog)).ToList();
        var loader = new FakeLoader();
        foreach (var a in artifacts)
        {
            loader.Set(a, Doc(a));
        }

        var rule = new ProbeRule("r", _ => true, a => new[]
        {
            F("i-" + a.Name, FindingSeverity.Information, "info " + a.Name),
            F("e-" + a.Name, FindingSeverity.Error, "error " + a.Name),
            F("w-" + a.Name, FindingSeverity.Warning, "warn " + a.Name),
        });

        var first = await new FindingsService(loader, new[] { rule }).EvaluateAsync(artifacts, null, CancellationToken.None);
        var second = await new FindingsService(loader, new[] { rule }).EvaluateAsync(artifacts.AsEnumerable().Reverse().ToList(), null, CancellationToken.None);

        Assert.Equal(
            first.Findings.Select(f => f.Severity),
            first.Findings.Select(f => f.Severity).OrderByDescending(s => s));
        Assert.Equal(FindingSeverity.Error, first.Findings[0].Severity);
        Assert.Equal(FindingSeverity.Information, first.Findings[^1].Severity);
        Assert.Equal(first.Findings.Select(f => f.Id), second.Findings.Select(f => f.Id));
    }

    [Fact]
    public async Task A_failing_rule_or_unreadable_artifact_is_reported_and_the_rest_still_run()
    {
        var good = Artifact("good.log", ArtifactType.TextLog);
        var broken = Artifact("broken.log", ArtifactType.TextLog);
        var unreadable = Artifact("unreadable.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(good, Doc(good));
        loader.Set(broken, Doc(broken));
        loader.Throw(unreadable, new IOException("disk gone"));

        var rules = new IDocumentRule[]
        {
            new ProbeRule("explodes", a => true, a => a.Name == "broken.log" ? throw new InvalidOperationException("bad rule") : Array.Empty<Finding>()),
            new ProbeRule("works", a => true, a => new[] { F("w-" + a.Name, FindingSeverity.Warning, "ok " + a.Name) }),
        };

        var result = await new FindingsService(loader, rules)
            .EvaluateAsync(new[] { good, broken, unreadable }, null, CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Issues, i => i.RuleId == "explodes" && i.Message.Contains("bad rule"));
        Assert.Contains(result.Issues, i => i.RuleId == "loader" && i.Message.Contains("disk gone"));
    }

    [Fact]
    public async Task A_parser_failure_message_is_surfaced_as_an_issue()
    {
        var artifact = Artifact("a.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Doc(artifact, "parser exploded"));
        var rule = new ProbeRule("r", _ => true, _ => Array.Empty<Finding>());

        var result = await new FindingsService(loader, new[] { rule }).EvaluateAsync(new[] { artifact }, null, CancellationToken.None);

        Assert.Equal("parser exploded", Assert.Single(result.Issues).Message);
    }

    [Fact]
    public async Task Cancellation_stops_evaluation()
    {
        var artifact = Artifact("a.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(artifact, Doc(artifact));
        var rule = new ProbeRule("r", _ => true, _ => Array.Empty<Finding>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FindingsService(loader, new[] { rule }).EvaluateAsync(new[] { artifact }, null, cts.Token));
    }

    [Fact]
    public async Task Progress_reaches_the_total()
    {
        var artifacts = Enumerable.Range(0, 5).Select(i => Artifact($"f{i}.log", ArtifactType.TextLog)).ToList();
        var loader = new FakeLoader();
        foreach (var a in artifacts)
        {
            loader.Set(a, Doc(a));
        }

        var reports = new List<FindingsProgress>();
        var progress = new SyncProgress(reports.Add);
        var rule = new ProbeRule("r", _ => true, _ => Array.Empty<Finding>());

        await new FindingsService(loader, new[] { rule }).EvaluateAsync(artifacts, progress, CancellationToken.None);

        Assert.Equal(5, reports.Max(r => r.ArtifactsEvaluated));
        Assert.All(reports, r => Assert.Equal(5, r.ArtifactsTotal));
    }

    private sealed class SyncProgress : IProgress<FindingsProgress>
    {
        private readonly Action<FindingsProgress> _report;

        public SyncProgress(Action<FindingsProgress> report) => _report = report;

        public void Report(FindingsProgress value)
        {
            lock (_report)
            {
                _report(value);
            }
        }
    }
}
