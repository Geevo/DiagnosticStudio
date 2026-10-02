using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Rules;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Rules;

public sealed class FileHealthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-health-" + Guid.NewGuid().ToString("N"));

    public FileHealthServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DocumentLoadResult Ok(DiagnosticDocument document, string? failure = null) => new(document, failure);

    private static Task<IReadOnlyList<FileProblem>> Check(FakeLoader loader, params DiagnosticArtifact[] artifacts) =>
        new FileHealthService(loader).CheckAsync(artifacts, null, CancellationToken.None);

    private static RegistryDocument RegistryWithIssues(DiagnosticArtifact artifact, params string[] lines) =>
        RegFileParser.Parse(artifact, lines, new ListLines(lines));

    // ---- healthy ----

    [Fact]
    public async Task Files_that_read_fine_produce_nothing()
    {
        var log = Artifact("a.log", ArtifactType.TextLog);
        var reg = Artifact("k.reg", ArtifactType.RegistryExport);
        var evtx = Artifact("s.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        loader.Set(log, Ok(Text(log, "line")));
        loader.Set(reg, Ok(RegistryWithIssues(reg, "Windows Registry Editor Version 5.00", "", "[HKEY_LOCAL_MACHINE\\X]", "\"a\"=\"b\"")));
        loader.Set(evtx, Ok(EventLog(evtx, new DataEventSource().Add("P", 1))));

        Assert.Empty(await Check(loader, log, reg, evtx));
    }

    [Fact]
    public async Task Archives_that_were_opened_are_not_files_to_check()
    {
        var container = Artifact("inner.zip", ArtifactType.Archive) with { IsContainer = true };
        var loader = new FakeLoader();

        Assert.Empty(await Check(loader, container));
        Assert.Empty(loader.Loaded);
    }

    // ---- failures ----

    [Fact]
    public async Task A_parser_failure_is_reported_as_failed_with_the_message()
    {
        var xml = Artifact("bad.xml", ArtifactType.Xml);
        var loader = new FakeLoader();
        loader.Set(xml, Ok(Text(xml, "<a><b></a>"), "Not well-formed XML: line 1, position 7"));

        var problem = Assert.Single(await Check(loader, xml));

        Assert.Equal(FileProblemKind.Failed, problem.Kind);
        Assert.Contains("Not well-formed XML", problem.Message);
        Assert.Contains("plain text", problem.Remedy);
        Assert.Equal(DiagnosticLocation.ForArtifact(xml.Id), problem.Location);
    }

    [Fact]
    public async Task A_file_that_throws_when_opened_is_reported_not_propagated()
    {
        var bad = Artifact("bad.log", ArtifactType.TextLog);
        var good = Artifact("good.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Throw(bad, new IOException("disk gone"));
        loader.Set(good, Ok(Text(good, "x")));

        var problem = Assert.Single(await Check(loader, bad, good));

        Assert.Equal(FileProblemKind.Failed, problem.Kind);
        Assert.Contains("disk gone", problem.Message);
    }

    [Fact]
    public async Task A_failed_file_that_also_has_no_viewer_is_listed_once()
    {
        var file = Artifact("x.bin", ArtifactType.Binary);
        var loader = new FakeLoader();
        loader.Set(file, Ok(new UnsupportedDocument { Artifact = file, Reason = "No viewer" }, "The parser crashed"));

        var problem = Assert.Single(await Check(loader, file));

        Assert.Equal(FileProblemKind.Failed, problem.Kind);
    }

    // ---- no viewer ----

    [Fact]
    public async Task A_file_with_no_viewer_says_why_and_what_to_do()
    {
        var etl = Artifact("trace.etl", ArtifactType.Trace);
        var loader = new FakeLoader();
        loader.Set(etl, Ok(new UnsupportedDocument { Artifact = etl, Reason = "ETL decoding is not supported yet." }));

        var problem = Assert.Single(await Check(loader, etl));

        Assert.Equal(FileProblemKind.NoViewer, problem.Kind);
        Assert.Equal("ETL decoding is not supported yet.", problem.Message);
        Assert.Contains("Notepad", problem.Remedy);
    }

    // ---- partly read ----

    [Fact]
    public async Task A_registry_export_with_unparsed_lines_points_at_the_first_one()
    {
        var reg = Artifact("k.reg", ArtifactType.RegistryExport);
        var doc = RegistryWithIssues(reg, "[HKEY_LOCAL_MACHINE\\X]", "\"a\"=\"b\"", "<stray/>", "<more/>", "\"c\"=\"d\"");
        var loader = new FakeLoader();
        loader.Set(reg, Ok(doc));

        var problem = Assert.Single(await Check(loader, reg));

        Assert.Equal(FileProblemKind.Partial, problem.Kind);
        Assert.Contains("2 lines could not be parsed. First at line 3", problem.Message);
        Assert.Equal(DiagnosticLocation.ForLine(reg.Id, 3), problem.Location);
    }

    [Fact]
    public async Task A_registry_export_that_hit_the_size_limit_says_where_parsing_stopped()
    {
        var reg = Artifact("big.reg", ArtifactType.RegistryExport);
        var doc = RegistryWithIssues(reg, "[HKEY_LOCAL_MACHINE\\X]") with { IsTruncated = true, TruncatedAtLine = 500_000 };
        var loader = new FakeLoader();
        loader.Set(reg, Ok(doc));

        var problem = Assert.Single(await Check(loader, reg));

        Assert.Equal(FileProblemKind.Partial, problem.Kind);
        Assert.Contains("stopped at line 500,000", problem.Message);
        Assert.Equal(DiagnosticLocation.ForLine(reg.Id, 500_000), problem.Location);
    }

    [Fact]
    public async Task An_event_log_with_damaged_records_or_not_cleanly_closed_is_partly_read()
    {
        var damaged = Artifact("a.evtx", ArtifactType.EventLog);
        var dirty = Artifact("b.evtx", ArtifactType.EventLog);
        var loader = new FakeLoader();
        loader.Set(damaged, Ok(EventLog(damaged, new DataEventSource().Add("P", 1)) with { TotalIssueCount = 3 }));
        loader.Set(dirty, Ok(EventLog(dirty, new DataEventSource().Add("P", 1)) with { IsDirty = true }));

        var problems = await Check(loader, damaged, dirty);

        Assert.Equal(FileProblemKind.Partial, problems.Single(p => p.Artifact.Id == damaged.Id).Kind);
        Assert.Equal(FileProblemKind.Caution, problems.Single(p => p.Artifact.Id == dirty.Id).Kind);
        Assert.Contains(problems, p => p.Artifact.Id == damaged.Id && p.Message.Contains("3 damaged"));
        Assert.Contains(problems, p => p.Artifact.Id == dirty.Id && p.Message.Contains("not cleanly closed"));
    }

    [Fact]
    public async Task Notes_on_a_trace_are_reported_as_a_caution_with_the_note_as_the_message()
    {
        var trace = Artifact("boot.etl", ArtifactType.Trace);
        var loader = new FakeLoader();
        loader.Set(trace, Ok(EventLog(trace, new DataEventSource().Add("P", 1)) with
        {
            Notes = new[] { "Only the first 300,000 are shown.", "12 events have no description on this machine." },
        }));

        var problems = await Check(loader, trace);

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(FileProblemKind.Caution, p.Kind));
        Assert.Contains(problems, p => p.Message == "Only the first 300,000 are shown.");
        Assert.DoesNotContain(problems, p => p.Message.Contains("damaged"));
    }

    // ---- empty ----

    [Fact]
    public async Task A_zero_byte_file_is_reported_empty_without_being_parsed()
    {
        var path = Path.Combine(_dir, "empty.log");
        File.WriteAllBytes(path, Array.Empty<byte>());
        var empty = Artifact("empty.log", ArtifactType.TextLog) with { ExtractedPath = path };
        var loader = new FakeLoader();

        var problem = Assert.Single(await Check(loader, empty));

        Assert.Equal(FileProblemKind.Empty, problem.Kind);
        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public async Task A_file_with_content_is_not_reported_empty()
    {
        var path = Path.Combine(_dir, "full.log");
        File.WriteAllText(path, "x");
        var full = Artifact("full.log", ArtifactType.TextLog) with { ExtractedPath = path };
        var loader = new FakeLoader();
        loader.Set(full, Ok(Text(full, "x")));

        Assert.Empty(await Check(loader, full));
    }

    // ---- order, progress, cancellation ----

    [Fact]
    public async Task Failed_files_come_first_then_by_where_they_are_in_the_bundle()
    {
        var b = Artifact("b.xml", ArtifactType.Xml);
        var a = Artifact("a.xml", ArtifactType.Xml);
        var etl = Artifact("t.etl", ArtifactType.Trace);
        var loader = new FakeLoader();
        loader.Set(b, Ok(Text(b, "x"), "bad b"));
        loader.Set(a, Ok(Text(a, "x"), "bad a"));
        loader.Set(etl, Ok(new UnsupportedDocument { Artifact = etl, Reason = "no" }));

        var problems = await Check(loader, etl, b, a);

        Assert.Equal(new[] { "a.xml", "b.xml", "t.etl" }, problems.Select(p => p.Artifact.Name));
    }

    [Fact]
    public async Task Progress_counts_every_file()
    {
        var files = Enumerable.Range(0, 5).Select(i => Artifact("f" + i + ".log", ArtifactType.TextLog)).ToArray();
        var loader = new FakeLoader();
        foreach (var f in files)
        {
            loader.Set(f, Ok(Text(f, "x")));
        }

        var reports = new List<FileHealthProgress>();
        await new FileHealthService(loader).CheckAsync(files, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(5, reports.Max(r => r.FilesChecked));
        Assert.All(reports, r => Assert.Equal(5, r.FilesTotal));
    }

    private sealed class SyncProgress : IProgress<FileHealthProgress>
    {
        private readonly Action<FileHealthProgress> _report;

        public SyncProgress(Action<FileHealthProgress> report) => _report = report;

        public void Report(FileHealthProgress value)
        {
            lock (this)
            {
                _report(value);
            }
        }
    }

    [Fact]
    public async Task Checking_can_be_cancelled()
    {
        var f = Artifact("a.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(f, Ok(Text(f, "x")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FileHealthService(loader).CheckAsync(new[] { f }, null, cts.Token));
    }
}
