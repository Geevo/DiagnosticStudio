using System.Runtime.CompilerServices;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.Search;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.Tests.App;

public sealed class SearchResultsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-svm-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();

    public SearchResultsViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- scripted service ----

    private sealed class ScriptedService : IGlobalSearchService
    {
        private readonly Func<IReadOnlyList<DiagnosticArtifact>, SearchQuery, SearchOptions, CancellationToken, IAsyncEnumerable<SearchUpdate>> _script;

        public ScriptedService(Func<IReadOnlyList<DiagnosticArtifact>, SearchQuery, SearchOptions, CancellationToken, IAsyncEnumerable<SearchUpdate>> script) =>
            _script = script;

        public List<(SearchQuery Query, SearchOptions Options)> Calls { get; } = new();

        public IAsyncEnumerable<SearchUpdate> SearchAsync(
            IReadOnlyList<DiagnosticArtifact> artifacts, SearchQuery query, SearchOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((query, options));
            return _script(artifacts, query, options, cancellationToken);
        }
    }

    private static DiagnosticArtifact Artifact(string name, string? path = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OriginalPath = name,
        ExtractedPath = path,
        Provenance = new[] { "Bundle.zip", name },
        ArtifactType = ArtifactType.TextLog,
    };

    private static SearchHit LineHit(DiagnosticArtifact a, int line, string preview = "needle here", int start = 0) =>
        new(a.Id, DiagnosticLocation.ForLine(a.Id, line), preview, start, 6);

    private static ArtifactSearchResult Result(DiagnosticArtifact a, int total, int shown, bool lowerBound = false) =>
        new(a, Enumerable.Range(1, shown).Select(i => LineHit(a, i)).ToList(), total, lowerBound);

    private static async IAsyncEnumerable<SearchUpdate> Updates(params SearchUpdate[] updates)
    {
        foreach (var u in updates)
        {
            await Task.Yield();
            yield return u;
        }
    }

    private (SearchResultsViewModel Vm, ScriptedService Service, WorkspaceViewModel Workspace, DocumentHostViewModel Host) Create(
        ScriptedService service, IReadOnlyList<DiagnosticArtifact>? artifacts = null)
    {
        var all = artifacts ?? Array.Empty<DiagnosticArtifact>();
        var workspace = new WorkspaceViewModel(new NoIngestor(), _output)
        {
            Current = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, all, Array.Empty<IngestionIssue>()),
        };
        var host = new DocumentHostViewModel(
            workspace,
            new DocumentLoader(new IDiagnosticParser[] { new TextLogParser(), new UnsupportedArtifactParser() }),
            new NavigationHistory(),
            _output);
        return (new SearchResultsViewModel(workspace, service, host, _output), service, workspace, host);
    }

    private sealed class NoIngestor : IBundleIngestor
    {
        public Task<InvestigationWorkspace> IngestAsync(string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // ---- results ----

    [Fact]
    public async Task Results_stream_in_grouped_by_artifact_and_ordered_by_hit_count()
    {
        var a = Artifact("a.log");
        var b = Artifact("b.log");
        var c = Artifact("c.log");
        var service = new ScriptedService((arts, _, _, _) => Updates(
            new SearchUpdate(Result(a, 2, 2), null, 1, 3, 2),
            new SearchUpdate(Result(b, 10, 10), null, 2, 3, 12),
            new SearchUpdate(Result(c, 2, 2), null, 3, 3, 14)));
        var (vm, _, _, _) = Create(service, new[] { a, b, c });
        var started = 0;
        vm.SearchStarted += (_, _) => started++;

        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.Equal(1, started);
        Assert.Equal(new[] { "b.log", "a.log", "c.log" }, vm.Results.Select(n => n.Title)); // count desc, then name
        Assert.Equal("10", vm.Results[0].CountText);
        Assert.Equal(10, vm.Results[0].Hits.Count);
        Assert.True(vm.HasResults);
        Assert.False(vm.IsSearching);
        Assert.Equal(3, vm.ArtifactsSearched);
        Assert.StartsWith("14 results in 3 artifacts", vm.StatusText);
        Assert.Contains("searched 3 artifacts", vm.StatusText);
    }

    [Fact]
    public async Task Hits_expose_location_text_and_preview_for_display()
    {
        var a = Artifact("a.log");
        var reg = Artifact("t.reg");
        var evt = Artifact("s.evtx");
        var service = new ScriptedService((_, _, _, _) => Updates(
            new SearchUpdate(
                new ArtifactSearchResult(a, new[] { LineHit(a, 18442, "x needle y", 2) }, 1), null, 1, 3, 1),
            new SearchUpdate(
                new ArtifactSearchResult(reg, new[]
                {
                    new SearchHit(reg.Id, DiagnosticLocation.ForRegistry(reg.Id, @"HKCU\K", "V"), "p", 0, 1, "value data"),
                }, 1), null, 2, 3, 2),
            new SearchUpdate(
                new ArtifactSearchResult(evt, new[]
                {
                    new SearchHit(evt.Id, DiagnosticLocation.ForEventRecord(evt.Id, 4821), "ev", 0, 1, "event"),
                }, 1), null, 3, 3, 3)));
        var (vm, _, _, _) = Create(service, new[] { a, reg, evt });

        vm.Query = "x";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        var byName = vm.Results.ToDictionary(n => n.Title);
        var line = byName["a.log"].Hits.Single();
        Assert.Equal(2, line.MatchStart);
        Assert.Equal("x needle y", line.Preview);
        Assert.StartsWith("Line ", line.LocationText);
        Assert.Contains("18", line.LocationText);
        Assert.Equal("Registry value data", byName["t.reg"].Hits.Single().LocationText);
        Assert.StartsWith("Event 4821", byName["s.evtx"].Hits.Single().LocationText);
    }

    [Fact]
    public async Task Truncated_artifacts_show_a_note_and_lower_bound_counts_are_marked()
    {
        var a = Artifact("big.log");
        var service = new ScriptedService((_, _, _, _) => Updates(
            new SearchUpdate(Result(a, 100_000, 500, lowerBound: true), null, 1, 1, 100_000)));
        var (vm, _, _, _) = Create(service, new[] { a });

        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        var node = Assert.Single(vm.Results);
        Assert.EndsWith("+", node.CountText);
        Assert.Equal(501, node.Hits.Count);
        var note = node.Hits[^1];
        Assert.True(note.IsPlaceholder);
        Assert.Null(note.Location);
        Assert.Contains("99,500+ more not listed", note.Preview.Replace(' ', ','));
    }

    [Fact]
    public async Task Few_results_start_expanded_and_a_flood_leaves_later_artifacts_collapsed()
    {
        var arts = Enumerable.Range(0, 5).Select(i => Artifact($"f{i}.log")).ToList();
        var service = new ScriptedService((_, _, _, _) => Updates(
            arts.Select((a, i) => new SearchUpdate(Result(a, 200, 200), null, i + 1, 5, (i + 1) * 200L)).ToArray()));
        var (vm, _, _, _) = Create(service, arts);

        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.True(vm.Results[0].IsExpanded);
        Assert.False(vm.Results[^1].IsExpanded);
    }

    [Fact]
    public async Task No_results_says_so_with_the_scope_of_the_search()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _, _, _) => Updates(new SearchUpdate(null, null, 1, 1, 0)));
        var (vm, _, _, _) = Create(service, new[] { a });

        vm.Query = "zzz";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.False(vm.HasResults);
        Assert.StartsWith("No results. Searched 1 artifacts", vm.StatusText);
    }

    // ---- input handling ----

    [Fact]
    public async Task A_bad_operator_is_reported_and_nothing_is_searched()
    {
        var service = new ScriptedService((_, _, _, _) => Updates());
        var (vm, svc, _, _) = Create(service);

        vm.Query = "level:loud";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.NotNull(vm.QueryError);
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public async Task An_empty_query_clears_the_results()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _, _, _) => Updates(new SearchUpdate(Result(a, 1, 1), null, 1, 1, 1)));
        var (vm, _, _, _) = Create(service, new[] { a });
        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;
        Assert.True(vm.HasResults);

        vm.Query = "  ";
        vm.SearchCommand.Execute(null);

        Assert.False(vm.HasResults);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public void Searching_without_an_open_bundle_asks_for_one()
    {
        var service = new ScriptedService((_, _, _, _) => Updates());
        var (vm, svc, workspace, _) = Create(service);
        workspace.Current = null;

        vm.Query = "x";
        vm.SearchCommand.Execute(null);

        Assert.Contains("Open an archive or folder", vm.StatusText);
        Assert.Empty(svc.Calls);
    }

    [Fact]
    public async Task The_parsed_query_and_the_options_reach_the_service_and_toggling_an_option_reruns_the_search()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _, _, _) => Updates(new SearchUpdate(null, null, 1, 1, 0)));
        var (vm, svc, _, _) = Create(service, new[] { a });

        vm.Query = "eventid:7031 crashed";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;
        Assert.Single(svc.Calls);
        Assert.Equal("crashed", svc.Calls[0].Query.Text);
        Assert.True(svc.Calls[0].Query.EventIds!.Contains(7031));
        Assert.False(svc.Calls[0].Options.IncludeEventMessages);
        Assert.True(svc.Calls[0].Options.IncludeFileNames);

        vm.IncludeEventMessages = true;
        await vm.PendingSearch;
        vm.MatchCase = true;
        await vm.PendingSearch;
        vm.IncludeFileNames = false;
        await vm.PendingSearch;

        Assert.Equal(4, svc.Calls.Count);
        Assert.True(svc.Calls[1].Options.IncludeEventMessages);
        Assert.True(svc.Calls[2].Query.MatchCase);
        Assert.False(svc.Calls[3].Options.IncludeFileNames);
    }

    [Fact]
    public void Changing_an_option_before_any_search_does_not_start_one()
    {
        var service = new ScriptedService((_, _, _, _) => Updates());
        var (vm, svc, _, _) = Create(service);
        vm.Query = "text";

        vm.MatchCase = true;

        Assert.Empty(svc.Calls);
    }

    // ---- failures, cancellation, supersession ----

    [Fact]
    public async Task Search_issues_go_to_the_output_panel_and_the_search_continues()
    {
        var a = Artifact("a.log");
        var bad = Artifact("bad.log");
        var service = new ScriptedService((_, _, _, _) => Updates(
            new SearchUpdate(null, new SearchIssue(bad, "Could not search Bundle.zip → bad.log: boom"), 1, 2, 0),
            new SearchUpdate(Result(a, 1, 1), null, 2, 2, 1)));
        var (vm, _, _, _) = Create(service, new[] { a, bad });

        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.Single(vm.Results);
        Assert.Contains(_output.Entries, e => e.Severity == OutputSeverity.Warning && e.Source == "Search" && e.Message.Contains("boom"));
    }

    [Fact]
    public async Task An_unexpected_service_failure_is_reported_not_thrown()
    {
        var service = new ScriptedService((_, _, _, _) => Throwing());
        var (vm, _, _, _) = Create(service, new[] { Artifact("a.log") });

        vm.Query = "x";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.False(vm.IsSearching);
        Assert.StartsWith("Search failed", vm.StatusText);
        Assert.Contains(_output.Entries, e => e.Severity == OutputSeverity.Error && e.Source == "Search");

        static async IAsyncEnumerable<SearchUpdate> Throwing()
        {
            await Task.Yield();
            throw new InvalidOperationException("kaboom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private static async IAsyncEnumerable<SearchUpdate> Slow(
        DiagnosticArtifact artifact, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SearchUpdate(Result(artifact, 1, 1), null, 1, 2, 1);
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    [Fact]
    public async Task Cancel_stops_a_running_search_and_keeps_the_partial_results()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _, _, ct) => Slow(a, ct));
        var (vm, _, _, _) = Create(service, new[] { a });

        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        for (var i = 0; i < 100 && vm.Results.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(vm.IsSearching);
        vm.CancelCommand.Execute(null);
        await vm.PendingSearch;

        Assert.False(vm.IsSearching);
        Assert.Single(vm.Results);
        Assert.EndsWith("(cancelled, partial results)", vm.StatusText);
    }

    [Fact]
    public async Task A_new_search_supersedes_a_running_one_and_only_the_newest_results_remain()
    {
        var first = Artifact("first.log");
        var second = Artifact("second.log");
        var service = new ScriptedService((_, query, _, ct) =>
            query.Text == "one" ? Slow(first, ct) : Updates(new SearchUpdate(Result(second, 3, 3), null, 1, 1, 3)));
        var (vm, _, _, _) = Create(service, new[] { first, second });

        vm.Query = "one";
        vm.SearchCommand.Execute(null);
        for (var i = 0; i < 100 && vm.Results.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        vm.Query = "two";
        vm.SearchCommand.Execute(null);
        await vm.PendingSearch;

        Assert.Equal(new[] { "second.log" }, vm.Results.Select(n => n.Title));
        Assert.False(vm.IsSearching);
        Assert.StartsWith("3 results", vm.StatusText);
    }

    [Fact]
    public async Task Opening_a_different_bundle_clears_the_results_and_cancels_the_search()
    {
        var a = Artifact("a.log");
        var service = new ScriptedService((_, _, _, ct) => Slow(a, ct));
        var (vm, _, workspace, _) = Create(service, new[] { a });
        vm.Query = "needle";
        vm.SearchCommand.Execute(null);
        for (var i = 0; i < 100 && vm.Results.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        workspace.Close();
        await vm.PendingSearch;

        Assert.Empty(vm.Results);
        Assert.False(vm.IsSearching);
        Assert.False(vm.HasResults);
    }

    // ---- navigation (real service and parsers) ----

    [Fact]
    public async Task Activating_a_text_hit_opens_the_file_at_the_line_and_highlights_the_query()
    {
        var path = Path.Combine(_dir, "agent.log");
        File.WriteAllLines(path, Enumerable.Range(1, 400).Select(i => i == 321 ? "2026-07-23 ERROR failed 0x80072F8F again" : $"line {i} ok"));
        var artifact = Artifact("agent.log", path);
        var loader = new DocumentLoader(new IDiagnosticParser[] { new TextLogParser(), new UnsupportedArtifactParser() });
        var (_, _, workspace, host) = Create(new ScriptedService((_, _, _, _) => Updates()), new[] { artifact });
        var real = new SearchResultsViewModel(workspace, new GlobalSearchService(loader), host, _output);

        real.Query = "0x80072f8f";
        real.SearchCommand.Execute(null);
        await real.PendingSearch;

        var node = Assert.Single(real.Results);
        var hit = node.Hits.Single(h => h.Location?.Kind == DiagnosticLocationKind.Line);
        Assert.Equal("Line 321", hit.LocationText);

        real.OpenHitCommand.Execute(hit);
        var document = host.Documents.OfType<ArtifactDocumentViewModel>().Single();
        for (var i = 0; i < 200 && document.IsLoading; i++)
        {
            await Task.Delay(25);
        }

        var viewer = Assert.IsType<TextViewerViewModel>(document.Viewer);
        await viewer.PendingSearch;
        Assert.Equal(321, viewer.CurrentLine);
        Assert.Equal("0x80072f8f", viewer.FindText);
        Assert.False(viewer.MatchCase);
        Assert.Equal(1, viewer.MatchCount);
        Assert.Same(document, host.ActiveDocument);
    }

    [Fact]
    public void Activating_an_artifact_opens_it_and_activating_a_placeholder_does_nothing()
    {
        var path = Path.Combine(_dir, "a.log");
        File.WriteAllText(path, "x");
        var artifact = Artifact("a.log", path);
        var (vm, _, _, host) = Create(new ScriptedService((_, _, _, _) => Updates()), new[] { artifact });
        var node = new SearchArtifactNodeViewModel(Result(artifact, 600, 500));

        vm.OpenHitCommand.Execute(node.Hits[^1]); // "... more not listed"
        Assert.Single(host.Documents); // still only the Overview

        vm.OpenArtifactCommand.Execute(node);

        Assert.Equal(2, host.Documents.Count);
        Assert.Equal("a.log", host.ActiveDocument!.Title);
    }
}
