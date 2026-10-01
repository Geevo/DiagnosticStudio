using System.IO.Compression;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

public class BundleIngestorTests
{
    private static Task<InvestigationWorkspace> Ingest(
        TestWorkspace ws, string input, ExtractionLimits? limits = null, CancellationToken ct = default) =>
        TestWorkspace.CreateIngestor().IngestAsync(input, ws.Options(limits), progress: null, ct);

    [Fact]
    public async Task Nested_archives_are_extracted_recursively_with_provenance()
    {
        using var ws = new TestWorkspace();
        var inner = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddText(z, "Logs/agent.log", "2026-07-23 14:12:00 hello");
            TestWorkspace.AddText(z, "Events/readme.txt", "x");
        });
        var bundle = TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "mdmlogs.zip", inner);
            TestWorkspace.AddText(z, "Command/ipconfig_all_output.log", "Windows IP Configuration");
        });
        File.WriteAllBytes(ws.PathFor("Bundle.zip"), bundle);

        using var result = await Ingest(ws, ws.PathFor("Bundle.zip"));

        var agent = Assert.Single(result.Artifacts, a => a.Name == "agent.log");
        Assert.Equal(new[] { "Bundle.zip", "mdmlogs.zip", "Logs", "agent.log" }, agent.Provenance);
        Assert.Equal(2, agent.NestingDepth);
        Assert.StartsWith(result.WorkingDirectory, agent.ExtractedPath);
        Assert.Equal("2026-07-23 14:12:00 hello", await File.ReadAllTextAsync(agent.ExtractedPath!));

        var nested = Assert.Single(result.Artifacts, a => a.Name == "mdmlogs.zip");
        Assert.True(nested.IsContainer);
        Assert.Equal(nested.Id, agent.ParentContainerId);
        Assert.True(result.Artifacts.Single(a => a.Name == "Bundle.zip").IsContainer);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Command_output_is_classified_by_name_and_folder()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("b.zip"), TestWorkspace.BuildZip(z =>
            TestWorkspace.AddText(z, "Command/ipconfig_all_output.log", "Windows IP Configuration")));

        using var result = await Ingest(ws, ws.PathFor("b.zip"));

        var ip = Assert.Single(result.Artifacts, a => a.Name == "ipconfig_all_output.log");
        Assert.Equal(ArtifactType.CommandOutput, ip.ArtifactType);
        Assert.Equal("Networking", ip.Category);
        Assert.Equal("IpConfig", ip.Subtype);
    }

    [Fact]
    public async Task Zip_slip_entries_are_skipped_and_reported_without_writing_outside_workspace()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("evil.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddText(z, "../escaped.txt", "pwned");
            TestWorkspace.AddText(z, @"..\..\escaped2.txt", "pwned");
            TestWorkspace.AddText(z, "/abs.txt", "pwned");
            TestWorkspace.AddText(z, "good.txt", "fine");
        }));

        using var result = await Ingest(ws, ws.PathFor("evil.zip"));

        Assert.Contains(result.Artifacts, a => a.Name == "good.txt");
        Assert.DoesNotContain(result.Artifacts, a => a.Name.StartsWith("escaped") || a.Name == "abs.txt");
        Assert.Equal(3, result.Issues.Count(i => i.Message.StartsWith("Entry skipped")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "escaped2.txt")));
        Assert.False(File.Exists(Path.Combine(ws.WorkRoot, "escaped.txt")));
    }

    [Fact]
    public async Task Total_size_limit_stops_extraction_but_keeps_what_was_extracted()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("big.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "a.log", new byte[600]);
            TestWorkspace.AddBytes(z, "b.log", new byte[600]);
            TestWorkspace.AddBytes(z, "c.log", new byte[600]);
        }));

        using var result = await Ingest(ws, ws.PathFor("big.zip"), new ExtractionLimits { MaxTotalExtractedBytes = 1000 });

        Assert.Contains(result.Artifacts, a => a.Name == "a.log");
        Assert.DoesNotContain(result.Artifacts, a => a.Name == "b.log");
        Assert.Contains(result.Issues, i => i.Severity == IngestionIssueSeverity.Error && i.Message.Contains("Total extracted size"));
        Assert.Empty(Directory.EnumerateFiles(result.WorkingDirectory, "b.log", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Single_file_limit_skips_only_the_offending_entry()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("s.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "huge.log", new byte[5000]);
            TestWorkspace.AddText(z, "small.log", "ok");
        }));

        using var result = await Ingest(ws, ws.PathFor("s.zip"), new ExtractionLimits { MaxSingleFileBytes = 1000 });

        Assert.DoesNotContain(result.Artifacts, a => a.Name == "huge.log");
        Assert.Contains(result.Artifacts, a => a.Name == "small.log");
        Assert.Single(result.Issues, i => i.Subject.EndsWith("huge.log"));
    }

    [Fact]
    public async Task File_count_limit_stops_extraction()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("many.zip"), TestWorkspace.BuildZip(z =>
        {
            for (var i = 0; i < 10; i++)
            {
                TestWorkspace.AddText(z, $"f{i}.txt", "x");
            }
        }));

        // The root zip itself counts as discovered but extraction reserves slots per extracted entry.
        using var result = await Ingest(ws, ws.PathFor("many.zip"), new ExtractionLimits { MaxFileCount = 3 });

        Assert.Equal(3, result.Artifacts.Count(a => a.Name.StartsWith('f')));
        Assert.Contains(result.Issues, i => i.Message.Contains("File count limit"));
    }

    [Fact]
    public async Task Nesting_depth_limit_leaves_deep_archives_unopened()
    {
        using var ws = new TestWorkspace();
        var level3 = TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "deep.txt", "x"));
        var level2 = TestWorkspace.BuildZip(z => TestWorkspace.AddBytes(z, "l3.zip", level3));
        var level1 = TestWorkspace.BuildZip(z => TestWorkspace.AddBytes(z, "l2.zip", level2));
        var root = TestWorkspace.BuildZip(z => TestWorkspace.AddBytes(z, "l1.zip", level1));
        File.WriteAllBytes(ws.PathFor("root.zip"), root);

        using var result = await Ingest(ws, ws.PathFor("root.zip"), new ExtractionLimits { MaxNestingDepth = 2 });

        // root(0) and l1(1) are opened; l2 sits at depth 2 and is not.
        Assert.DoesNotContain(result.Artifacts, a => a.Name == "deep.txt");
        var l2 = Assert.Single(result.Artifacts, a => a.Name == "l2.zip");
        Assert.False(l2.IsContainer);
        Assert.Contains(result.Issues, i => i.Message.Contains("nesting depth"));
    }

    [Fact]
    public async Task Corrupt_nested_archive_is_reported_and_siblings_still_ingest()
    {
        using var ws = new TestWorkspace();
        var corrupt = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        File.WriteAllBytes(ws.PathFor("mixed.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "broken.zip", corrupt);
            TestWorkspace.AddText(z, "readable.log", "still here");
        }));

        using var result = await Ingest(ws, ws.PathFor("mixed.zip"));

        Assert.Contains(result.Artifacts, a => a.Name == "readable.log");
        var broken = Assert.Single(result.Artifacts, a => a.Name == "broken.zip");
        Assert.False(broken.IsContainer);
        Assert.Contains(result.Issues, i => i.Severity == IngestionIssueSeverity.Error && i.Subject.EndsWith("broken.zip"));
    }

    [Fact]
    public async Task Cab_is_listed_but_reported_as_unopened_when_no_provider_exists()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("c.zip"), TestWorkspace.BuildZip(z =>
            TestWorkspace.AddBytes(z, "logs.cab", new byte[] { 0x4D, 0x53, 0x43, 0x46, 0, 0, 0, 0 })));

        // A ZIP-only ingestor: the CAB provider would (correctly) open this one.
        using var result = await new BundleIngestor(new IArchiveProvider[] { new ZipArchiveProvider() })
            .IngestAsync(ws.PathFor("c.zip"), ws.Options(), progress: null, CancellationToken.None);

        var cab = Assert.Single(result.Artifacts, a => a.Name == "logs.cab");
        Assert.Equal(ArtifactType.Archive, cab.ArtifactType);
        Assert.False(cab.IsContainer);
        Assert.Contains(result.Issues, i => i.Severity == IngestionIssueSeverity.Information && i.Subject.EndsWith("logs.cab"));
    }

    [Fact]
    public async Task Directory_input_is_read_in_place_and_nested_zips_are_expanded_into_the_workspace()
    {
        using var ws = new TestWorkspace();
        var dir = ws.PathFor("extracted");
        Directory.CreateDirectory(Path.Combine(dir, "Logs"));
        File.WriteAllText(Path.Combine(dir, "Logs", "agent.log"), "line");
        File.WriteAllBytes(
            Path.Combine(dir, "more.zip"),
            TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "inner.log", "inner")));

        var result = await Ingest(ws, dir);

        var agent = Assert.Single(result.Artifacts, a => a.Name == "agent.log");
        Assert.Equal(new[] { "extracted", "Logs", "agent.log" }, agent.Provenance);
        Assert.Equal(Path.Combine(dir, "Logs", "agent.log"), agent.ExtractedPath);

        var inner = Assert.Single(result.Artifacts, a => a.Name == "inner.log");
        Assert.Equal(new[] { "extracted", "more.zip", "inner.log" }, inner.Provenance);
        Assert.StartsWith(result.WorkingDirectory, inner.ExtractedPath);

        result.Dispose();

        Assert.False(Directory.Exists(result.WorkingDirectory));
        Assert.True(File.Exists(Path.Combine(dir, "Logs", "agent.log")), "original input must never be deleted");
        Assert.True(File.Exists(Path.Combine(dir, "more.zip")));
    }

    [Fact]
    public async Task Disposing_the_workspace_removes_only_the_working_directory()
    {
        using var ws = new TestWorkspace();
        var zipPath = ws.PathFor("b.zip");
        File.WriteAllBytes(zipPath, TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "a.log", "x")));

        var result = await Ingest(ws, zipPath);
        Assert.True(Directory.Exists(result.WorkingDirectory));

        result.Dispose();

        Assert.False(Directory.Exists(result.WorkingDirectory));
        Assert.True(File.Exists(zipPath));
    }

    [Fact]
    public async Task Cancellation_cleans_up_the_working_directory()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("b.zip"), TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "a.log", "x")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ingest(ws, ws.PathFor("b.zip"), ct: cts.Token));

        Assert.False(Directory.Exists(ws.WorkRoot) && Directory.EnumerateDirectories(ws.WorkRoot).Any());
    }

    [Fact]
    public async Task Missing_input_throws()
    {
        using var ws = new TestWorkspace();

        await Assert.ThrowsAsync<FileNotFoundException>(() => Ingest(ws, ws.PathFor("nope.zip")));
    }

    [Fact]
    public async Task Progress_is_reported_through_to_completion()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("b.zip"), TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "a.log", "x")));
        var reports = new List<IngestionProgress>();
        var progress = new SyncProgress<IngestionProgress>(reports.Add);

        using var result = await TestWorkspace.CreateIngestor()
            .IngestAsync(ws.PathFor("b.zip"), ws.Options(), progress, CancellationToken.None);

        Assert.Equal(IngestionStage.Validating, reports.First().Stage);
        Assert.Equal(IngestionStage.Completed, reports.Last().Stage);
        Assert.Equal(2, reports.Last().ArtifactsDiscovered);
    }

    [Fact]
    public async Task Office_zip_containers_are_not_treated_as_archives()
    {
        using var ws = new TestWorkspace();
        var docx = TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "word/document.xml", "<x/>"));
        File.WriteAllBytes(ws.PathFor("b.zip"), TestWorkspace.BuildZip(z => TestWorkspace.AddBytes(z, "report.docx", docx)));

        using var result = await Ingest(ws, ws.PathFor("b.zip"));

        var report = Assert.Single(result.Artifacts, a => a.Name == "report.docx");
        Assert.NotEqual(ArtifactType.Archive, report.ArtifactType);
        Assert.DoesNotContain(result.Artifacts, a => a.Name == "document.xml");
    }

    [Fact]
    public async Task Evtx_and_registry_artifacts_are_classified()
    {
        using var ws = new TestWorkspace();
        File.WriteAllBytes(ws.PathFor("b.zip"), TestWorkspace.BuildZip(z =>
        {
            TestWorkspace.AddBytes(z, "Events/System.evtx", new byte[] { 0x45, 0x6C, 0x66, 0x46, 0x69, 0x6C, 0x65, 0 });
            TestWorkspace.AddText(z, "Reg/Internet_Settings.reg", "Windows Registry Editor Version 5.00");
            TestWorkspace.AddBytes(z, "trace.etl", new byte[] { 1, 2, 3 });
        }));

        using var result = await Ingest(ws, ws.PathFor("b.zip"));

        Assert.Equal(ArtifactType.EventLog, result.Artifacts.Single(a => a.Name == "System.evtx").ArtifactType);
        Assert.Equal("System", result.Artifacts.Single(a => a.Name == "System.evtx").Subtype);
        Assert.Equal(ArtifactType.RegistryExport, result.Artifacts.Single(a => a.Name == "Internet_Settings.reg").ArtifactType);
        Assert.Equal(ArtifactType.Trace, result.Artifacts.Single(a => a.Name == "trace.etl").ArtifactType);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
