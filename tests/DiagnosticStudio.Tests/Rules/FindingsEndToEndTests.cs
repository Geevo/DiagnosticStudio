using System.Text;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Tests.Evtx;
using DiagnosticStudio.Tests.Ingestion;

namespace DiagnosticStudio.Tests.Rules;

/// <summary>A nested bundle through the real ingestor and parsers, evaluated by the real default ruleset.</summary>
public sealed class FindingsEndToEndTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);
    private readonly TestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private static byte[] Utf16Reg(params string[] lines) =>
        new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(string.Join("\r\n", lines))).ToArray();

    private static DocumentLoader RealLoader() => new(new IDiagnosticParser[]
    {
        new RegFileParser(), new EvtxParser(), new TextLogParser(), new UnsupportedArtifactParser(),
    });

    [Fact]
    public async Task The_default_ruleset_finds_the_planted_conditions_and_every_evidence_location_resolves()
    {
        var system = new EvtxBuilder().Build(new[]
        {
            new TestEvent(11, "Service Control Manager", 7036, EventLevels.Information, T0, "PC", "Agent Service", "running"),
            new TestEvent(12, "Service Control Manager", 7031, EventLevels.Error, T0.AddMinutes(1), "PC", "Agent Service", "1"),
            new TestEvent(13, "Service Control Manager", 7031, EventLevels.Error, T0.AddMinutes(5), "PC", "Agent Service", "2"),
            new TestEvent(14, "Application Error", 1000, EventLevels.Error, T0.AddMinutes(6), "PC", "Agent.exe", "1.0"),
        });
        var inner = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "Events/System.evtx", system);
            TestWorkspace.AddText(
                z,
                "Logs/agent.log",
                string.Join(
                    "\n",
                    "2026-07-23 14:12:00 INFO start",
                    "2026-07-23 14:12:01 ERROR upload 1 failed: timeout",
                    "2026-07-23 14:12:02 ERROR upload 2 failed: timeout",
                    "2026-07-23 14:12:03 ERROR upload 3 failed: timeout") + "\n");
        });
        var bundle = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "mdmlogs.zip", inner);
            TestWorkspace.AddBytes(z, "Registry/system.reg", Utf16Reg(
                "Windows Registry Editor Version 5.00",
                "",
                @"[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending]",
                "@=\"\""));
        });
        var zipPath = _ws.PathFor("Bundle.zip");
        File.WriteAllBytes(zipPath, bundle);

        using var workspace = await TestWorkspace.CreateIngestor().IngestAsync(zipPath, _ws.Options(), null, CancellationToken.None);
        var loader = RealLoader();
        var service = new FindingsService(loader, DefaultRules.Create());

        var result = await service.EvaluateAsync(workspace.Artifacts, null, CancellationToken.None);

        Assert.Empty(result.Issues);
        Assert.Contains(result.Findings, f => f.Title == "Service 'Agent Service' terminated unexpectedly 2 times" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(result.Findings, f => f.Title == "Agent.exe crashed" && f.Severity == FindingSeverity.Error);
        Assert.Contains(result.Findings, f => f.Title.Contains("Component Based Servicing"));
        Assert.Contains(result.Findings, f => f.Title.Contains("Repeated error in agent.log: 3 occurrences"));
        Assert.Equal(FindingSeverity.Error, result.Findings[0].Severity);

        // Every piece of evidence points at something that really exists in the parsed artifact.
        foreach (var evidence in result.Findings.SelectMany(f => f.Evidence))
        {
            var artifact = workspace.Artifacts.Single(a => a.Id == evidence.Location.ArtifactId);
            var document = (await loader.LoadAsync(artifact, CancellationToken.None)).Document;
            switch (evidence.Location.Kind)
            {
                case DiagnosticLocationKind.EventRecord:
                    Assert.NotEqual(-1, ((EventLogDocument)document).Source.FindByRecordId(evidence.Location.NumericPosition!.Value));
                    break;
                case DiagnosticLocationKind.Line:
                    var lineNumber = (int)evidence.Location.NumericPosition!.Value;
                    Assert.Contains("ERROR", ((TextDocument)document).Lines.ReadLines(lineNumber - 1, 1)[0]);
                    break;
                case DiagnosticLocationKind.Registry:
                    Assert.NotNull(((RegistryDocument)document).FindKey(evidence.Location.Identifier!));
                    break;
                default:
                    Assert.Fail("Unexpected evidence kind " + evidence.Location.Kind);
                    break;
            }
        }
    }

    [Fact]
    public async Task A_bundle_with_nothing_notable_yields_no_findings_and_no_issues()
    {
        var bundle = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddText(z, "Logs/agent.log", "2026-07-23 14:12:00 INFO all good\n");
            TestWorkspace.AddBytes(z, "tools/helper.exe", new byte[] { 0x4D, 0x5A, 0, 0, 1, 2, 3, 4 });
        });
        var zipPath = _ws.PathFor("Quiet.zip");
        File.WriteAllBytes(zipPath, bundle);

        using var workspace = await TestWorkspace.CreateIngestor().IngestAsync(zipPath, _ws.Options(), null, CancellationToken.None);
        var result = await new FindingsService(RealLoader(), DefaultRules.Create())
            .EvaluateAsync(workspace.Artifacts, null, CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Issues);
    }
}
