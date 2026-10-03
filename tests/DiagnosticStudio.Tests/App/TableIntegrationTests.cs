using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.TableViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Timeline;
using DiagnosticStudio.Ingestion;
using DiagnosticStudio.Parsers.Tables;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Rules.Rules;
using DiagnosticStudio.Search;
using DiagnosticStudio.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

/// <summary>CMTrace logs and CSV files, as tables, take part in everything else: classification, search, rules, timeline, file checks.</summary>
public sealed class TableIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-tint-" + Guid.NewGuid().ToString("N"));

    public TableIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Rec(string message, string type = "1", string time = "14:12:00.000+000", string date = "7-23-2026") =>
        $"<![LOG[{message}]LOG]!><time=\"{time}\" date=\"{date}\" component=\"Agent\" context=\"\" type=\"{type}\" thread=\"7\" file=\"\">";

    private static TableDocument CmDocument(DiagnosticArtifact artifact, params string[] lines)
    {
        var raw = new ListLines(lines);
        var table = CmTraceTable.Build(raw);
        return new TableDocument
        {
            Artifact = artifact,
            Format = TableFormat.CmTrace,
            Table = table,
            RawSource = raw,
            UnreadLines = table.UnreadLines,
            FirstUnreadLine = table.FirstUnreadLine,
        };
    }

    private static TableDocument CsvDocument(DiagnosticArtifact artifact, params string[] lines)
    {
        var raw = new ListLines(lines);
        return new TableDocument { Artifact = artifact, Format = TableFormat.Csv, Table = CsvTable.Build(raw), RawSource = raw };
    }

    // ---- classification ----

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Theory]
    [InlineData("report.csv")]
    [InlineData("REPORT.CSV")]
    [InlineData("export.tsv")]
    public void Delimited_text_files_are_classified_as_csv(string name)
    {
        var path = Write(name, System.Text.Encoding.UTF8.GetBytes("a,b\r\n1,2\r\n"));

        var result = ArtifactClassifier.Classify(name, new[] { "Bundle", name }, path);

        Assert.Equal(ArtifactType.Csv, result.Type);
    }

    [Fact]
    public void A_csv_file_that_is_not_text_is_binary_and_an_empty_one_is_still_csv()
    {
        var binary = Write("odd.csv", new byte[] { 0, 1, 2, 3, 0, 255, 254, 0, 1, 0, 0, 0 });
        var empty = Write("empty.csv", Array.Empty<byte>());

        Assert.Equal(ArtifactType.Binary, ArtifactClassifier.Classify("odd.csv", new[] { "B", "odd.csv" }, binary).Type);
        Assert.Equal(ArtifactType.Csv, ArtifactClassifier.Classify("empty.csv", new[] { "B", "empty.csv" }, empty).Type);
    }

    // ---- search ----

    [Fact]
    public async Task Search_finds_text_in_a_cmtrace_table_and_a_csv_table_by_raw_line()
    {
        // Search only opens files that exist, so these have a path.
        var log = Artifact("agent.log", ArtifactType.TextLog) with { ExtractedPath = "agent.log" };
        var csv = Artifact("data.csv", ArtifactType.Csv) with { ExtractedPath = "data.csv" };
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(CmDocument(log, Rec("starting"), Rec("needle in a log")), null));
        loader.Set(csv, new DocumentLoadResult(CsvDocument(csv, "name,note", "a,x", "b,needle in a cell"), null));

        var hits = new List<SearchHit>();
        await foreach (var update in new GlobalSearchService(loader).SearchAsync(
                           new[] { log, csv }, SearchQueryParser.Parse("needle").Query, new SearchOptions(), CancellationToken.None))
        {
            if (update.Result is { } result)
            {
                hits.AddRange(result.Hits);
            }
        }

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Location == DiagnosticLocation.ForLine(log.Id, 2));
        Assert.Contains(hits, h => h.Location == DiagnosticLocation.ForLine(csv.Id, 3));
    }

    // ---- rules ----

    [Fact]
    public void The_repeated_error_rule_reads_cmtrace_logs()
    {
        var log = Artifact("IntuneManagementExtension.log", ArtifactType.TextLog);
        var doc = CmDocument(
            log,
            Rec("[Win32App] LogonUser failed with error 1326", "3", "14:00:01.000+000"),
            Rec("fine", "1"),
            Rec("[Win32App] LogonUser failed with error 1326", "3", "14:00:05.000+000"),
            Rec("[Win32App] LogonUser failed with error 1326", "3", "14:00:09.000+000"));

        var findings = new RepeatedLogErrorRule().Evaluate(log, doc, CancellationToken.None).ToList();

        var finding = Assert.Single(findings);
        Assert.Contains("3 occurrences", finding.Title);
        Assert.Equal(1, finding.Evidence[0].Location.NumericPosition);

        // The finding shows the message, not the markers and attributes around it.
        Assert.Contains("first at line 1: [Win32App] LogonUser failed with error 1326", finding.Description);
        Assert.DoesNotContain("<![LOG[", finding.Description);
        Assert.DoesNotContain("type=", finding.Description);
    }

    // ---- timeline ----

    [Fact]
    public async Task A_cmtrace_log_contributes_its_timestamped_lines_to_the_timeline_with_levels()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(
            CmDocument(log, Rec("one", "1", "14:00:01.000+000"), Rec("two", "3", "14:00:02.000+000"), Rec("three", "2", "14:00:03.000+000")),
            null));
        var service = new TimelineService(loader);

        var index = await service.BuildAsync(new[] { log }, null, CancellationToken.None);

        Assert.Equal(3, index.Count);
        Assert.Equal(new[] { LogSeverity.Information, LogSeverity.Error, LogSeverity.Warning }, index.Entries.Select(e => e.Severity));
        Assert.All(index.Entries, e => Assert.True(e.Unzoned));
        Assert.Equal(new DateTime(2026, 7, 23, 14, 0, 2).Ticks, index.Entries[1].Ticks);
        Assert.StartsWith("<![LOG[two]LOG]!>", await service.DescribeAsync(index, index.Entries[1], CancellationToken.None));
    }

    [Fact]
    public async Task A_csv_file_is_not_put_on_the_timeline()
    {
        var csv = Artifact("data.csv", ArtifactType.Csv);
        var loader = new FakeLoader();
        loader.Set(csv, new DocumentLoadResult(CsvDocument(csv, "when,what", "2026-07-23 14:00:00,x"), null));

        var index = await new TimelineService(loader).BuildAsync(new[] { csv }, null, CancellationToken.None);

        Assert.Empty(index.Sources);
        Assert.Empty(loader.Loaded);
    }

    // ---- file health ----

    [Fact]
    public async Task Lines_that_belong_to_no_record_are_reported_with_the_first_one()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(CmDocument(log, "stray header", Rec("a"), "stray", Rec("b")), null));

        var problem = Assert.Single(await new FileHealthService(loader).CheckAsync(new[] { log }, null, CancellationToken.None));

        Assert.Equal(FileProblemKind.Caution, problem.Kind);
        Assert.Contains("2 lines are not part of any record. First at line 1", problem.Message);
        Assert.Equal(DiagnosticLocation.ForLine(log.Id, 1), problem.Location);
    }

    [Fact]
    public async Task A_clean_table_is_not_a_problem()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var csv = Artifact("data.csv", ArtifactType.Csv);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(CmDocument(log, Rec("a"), Rec("b")), null));
        loader.Set(csv, new DocumentLoadResult(CsvDocument(csv, "a,b", "1,2"), null));

        Assert.Empty(await new FileHealthService(loader).CheckAsync(new[] { log, csv }, null, CancellationToken.None));
    }

    // ---- the application ----

    [Fact]
    public async Task Opening_a_table_artifact_shows_the_table_viewer_and_it_knows_its_place_for_the_timeline()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(CmDocument(log, Rec("a"), Rec("b")), null));
        var workspace = new WorkspaceViewModel(new Ingestor(new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { log }, Array.Empty<IngestionIssue>())), new OutputViewModel());
        var host = new DocumentHostViewModel(workspace, loader, new NavigationHistory(), new OutputViewModel());
        await workspace.OpenAsync(_dir);

        host.OpenArtifact(log);
        var document = host.Documents.OfType<ArtifactDocumentViewModel>().Single();
        for (var i = 0; i < 200 && document.IsLoading; i++)
        {
            await Task.Delay(10);
        }

        var viewer = Assert.IsType<TableViewerViewModel>(document.Viewer);
        viewer.SelectedRow = viewer.Rows[1];
        Assert.Equal(DiagnosticLocation.ForLine(log.Id, 2), viewer.CurrentPosition(log.Id));
        Assert.False(document.IsEmpty);

        // A line link from search, findings or the timeline reaches the record.
        host.OpenLocation(DiagnosticLocation.ForLine(log.Id, 1));
        Assert.Equal(0, viewer.SelectedRow!.Row);
    }

    private sealed class Ingestor : IBundleIngestor
    {
        private readonly InvestigationWorkspace _workspace;

        public Ingestor(InvestigationWorkspace workspace) => _workspace = workspace;

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(_workspace);
    }

    [Fact]
    public async Task The_document_cache_counts_a_tables_records()
    {
        var log = Artifact("agent.log", ArtifactType.TextLog);
        var loader = new FakeLoader();
        loader.Set(log, new DocumentLoadResult(CmDocument(log, Enumerable.Range(0, 1000).Select(i => Rec("r" + i)).ToArray()), null));
        var cache = new CachingDocumentLoader(loader);

        await cache.LoadAsync(log, CancellationToken.None);

        // The cache counts a document just after handing it to the caller.
        for (var i = 0; i < 300 && cache.Statistics.EstimatedBytes == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(cache.Statistics.EstimatedBytes >= 1000 * 24);
    }

    [Theory]
    [InlineData("<![LOG[disk failed]LOG]!><time=\"1\" type=\"3\">", "disk failed")]
    [InlineData("<![LOG[ spaced  ]LOG]!><time=\"1\">", "spaced")]
    [InlineData("<![LOG[multi-line start without an end", "multi-line start without an end")]
    [InlineData("2026-07-23 14:12:00 ERROR plain line", "2026-07-23 14:12:00 ERROR plain line")]
    public void A_cmtrace_record_is_sampled_as_its_message_and_other_lines_as_they_are(string line, string expected)
    {
        Assert.Equal(expected, RepeatedLogErrorRule.Sample(line));
    }
}
