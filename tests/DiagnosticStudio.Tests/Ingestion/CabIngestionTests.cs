using System.Text;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

/// <summary>Cabinets inside the real ingestion pipeline: nesting, provenance, classification and failure handling.</summary>
public sealed class CabIngestionTests : IDisposable
{
    private readonly TestWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private Task<InvestigationWorkspace> Ingest(string path) =>
        TestWorkspace.CreateIngestor().IngestAsync(path, _ws.Options(), progress: null, CancellationToken.None);

    [Fact]
    public async Task A_cabinet_inside_a_zip_is_opened_with_the_full_provenance_chain()
    {
        var cab = new CabBuilder()
            .Add(@"Logs\agent.log", "2026-07-23 14:12:00 ERROR failed")
            .Add(@"Events\notes.txt", "x")
            .Build();
        File.WriteAllBytes(_ws.PathFor("Bundle.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "mdmlogs.cab", cab);
            TestWorkspace.AddText(z, "Command/ipconfig_all_output.log", "Windows IP Configuration");
        }));

        using var result = await Ingest(_ws.PathFor("Bundle.zip"));

        var agent = Assert.Single(result.Artifacts, a => a.Name == "agent.log");
        Assert.Equal(new[] { "Bundle.zip", "mdmlogs.cab", "Logs", "agent.log" }, agent.Provenance);
        Assert.Equal(2, agent.NestingDepth);
        Assert.Equal(ArtifactType.TextLog, agent.ArtifactType);
        Assert.StartsWith(result.WorkingDirectory, agent.ExtractedPath);
        Assert.Equal("2026-07-23 14:12:00 ERROR failed", await File.ReadAllTextAsync(agent.ExtractedPath!));

        var container = Assert.Single(result.Artifacts, a => a.Name == "mdmlogs.cab");
        Assert.True(container.IsContainer);
        Assert.Equal(ArchiveFormats.Cab, container.Subtype);
        Assert.Equal(container.Id, agent.ParentContainerId);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task A_zip_inside_a_cabinet_inside_a_zip_is_followed_all_the_way_down()
    {
        var innerZip = TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "deep/readme.log", "2026-07-23 14:12:00 INFO deep"));
        var cab = new CabBuilder().Add("inner.zip", innerZip).Build();
        File.WriteAllBytes(_ws.PathFor("Bundle.zip"), TestWorkspace.BuildZip(z => TestWorkspace.AddBytes(z, "outer.cab", cab)));

        using var result = await Ingest(_ws.PathFor("Bundle.zip"));

        var deep = Assert.Single(result.Artifacts, a => a.Name == "readme.log");
        Assert.Equal(new[] { "Bundle.zip", "outer.cab", "inner.zip", "deep", "readme.log" }, deep.Provenance);
        Assert.Equal(3, deep.NestingDepth);
    }

    [Fact]
    public async Task A_cabinet_can_be_opened_directly_as_the_bundle()
    {
        File.WriteAllBytes(_ws.PathFor("mdmlogs.cab"), new CabBuilder().Add("Logs/agent.log", "2026-07-23 14:12:00 INFO hi").Build());

        using var result = await Ingest(_ws.PathFor("mdmlogs.cab"));

        var agent = Assert.Single(result.Artifacts, a => a.Name == "agent.log");
        Assert.Equal(new[] { "mdmlogs.cab", "Logs", "agent.log" }, agent.Provenance);
        Assert.True(result.Artifacts.Single(a => a.Name == "mdmlogs.cab").IsContainer);
    }

    [Fact]
    public async Task A_corrupt_cabinet_is_kept_as_a_file_and_reported_while_the_rest_of_the_bundle_loads()
    {
        File.WriteAllBytes(_ws.PathFor("Bundle.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "broken.cab", Encoding.ASCII.GetBytes("MSCF but then nothing sensible follows here"));
            TestWorkspace.AddText(z, "Logs/agent.log", "2026-07-23 14:12:00 INFO fine");
        }));

        using var result = await Ingest(_ws.PathFor("Bundle.zip"));

        var broken = Assert.Single(result.Artifacts, a => a.Name == "broken.cab");
        Assert.False(broken.IsContainer);
        Assert.Contains(result.Issues, i =>
            i.Severity == IngestionIssueSeverity.Error && i.Component == "CAB" && i.Subject.Contains("broken.cab"));
        Assert.Contains(result.Artifacts, a => a.Name == "agent.log");
    }

    [Fact]
    public async Task Hostile_names_inside_a_nested_cabinet_never_reach_outside_the_workspace()
    {
        var cab = new CabBuilder().Add(@"..\..\..\escaped.txt", "pwned").Add("ok.log", "2026-07-23 14:12:00 INFO ok").Build();
        File.WriteAllBytes(_ws.PathFor("Bundle.zip"), TestWorkspace.BuildZip(z => TestWorkspace.AddBytes(z, "x.cab", cab)));

        using var result = await Ingest(_ws.PathFor("Bundle.zip"));

        Assert.Contains(result.Artifacts, a => a.Name == "ok.log");
        Assert.DoesNotContain(result.Artifacts, a => a.Name == "escaped.txt");
        Assert.Contains(result.Issues, i => i.Severity == IngestionIssueSeverity.Warning && i.Component == "CAB");
        Assert.Empty(Directory.EnumerateFiles(_ws.Root, "escaped.txt", SearchOption.AllDirectories));
    }
}
