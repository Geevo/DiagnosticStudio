using DiagnosticStudio.App.ViewModels.EventLogViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Search;
using DiagnosticStudio.Timeline;

namespace DiagnosticStudio.Tests.Parsers;

public sealed class EtlIntegrationTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Parsers", "Fixtures", "small-trace.etl");

    private static DiagnosticArtifact Trace() => new()
    {
        Id = Guid.NewGuid(),
        Name = "boot.etl",
        OriginalPath = "boot.etl",
        ExtractedPath = Fixture,
        Provenance = new[] { "Bundle.zip", "Traces", "boot.etl" },
        ArtifactType = ArtifactType.Trace,
        Size = new FileInfo(Fixture).Length,
    };

    private static DocumentLoader Loader() =>
        new(new IDiagnosticParser[] { new EtlParser(), new UnsupportedArtifactParser() });

    [Fact]
    public async Task The_loader_gives_a_trace_an_event_log_instead_of_the_unsupported_notice()
    {
        var loaded = await Loader().LoadAsync(Trace(), CancellationToken.None);

        Assert.Null(loaded.FailureMessage);
        Assert.IsType<EventLogDocument>(loaded.Document);
    }

    [Fact]
    public async Task The_event_viewer_lists_the_events_of_a_trace()
    {
        var loaded = await Loader().LoadAsync(Trace(), CancellationToken.None);
        var document = (EventLogDocument)loaded.Document;

        var vm = new EventLogViewerViewModel(document with { Notes = new[] { "Only the first 3 are shown." } });

        Assert.Equal(4, vm.Events.Count);
        Assert.Contains("Only the first 3 are shown.", vm.WarningText);
        Assert.Contains("ClrInstanceID", vm.Events[2].MessagePreview);
        Assert.Contains(vm.ProviderOptions, p => p.Label.StartsWith("Microsoft-Windows-DotNETRuntime"));
    }

    [Fact]
    public async Task Search_finds_text_inside_a_trace_and_points_at_the_event()
    {
        var artifact = Trace();
        var service = new GlobalSearchService(Loader());

        var results = new List<ArtifactSearchResult>();
        await foreach (var update in service.SearchAsync(
                           new[] { artifact }, SearchQueryParser.Parse("ClrInstanceID").Query, new SearchOptions(), CancellationToken.None))
        {
            Assert.Null(update.Issue);
            if (update.Result is not null)
            {
                results.Add(update.Result);
            }
        }

        var result = Assert.Single(results);
        Assert.True(result.TotalHits >= 2);
        Assert.All(result.Hits, h => Assert.Equal(artifact.Id, h.ArtifactId));
    }

    [Fact]
    public async Task The_timeline_includes_the_events_of_a_trace()
    {
        var index = await new TimelineService(Loader()).BuildAsync(new[] { Trace() }, null, CancellationToken.None);

        var source = Assert.Single(index.Sources);
        Assert.Equal(4, source.EntryCount);
    }

    [Fact]
    public async Task The_cache_counts_what_a_trace_holds_in_memory()
    {
        var artifact = Trace();
        var loaded = await Loader().LoadAsync(artifact, CancellationToken.None);

        var estimate = DocumentSizeEstimator.Estimate(artifact, loaded.Document);

        var source = Assert.IsAssignableFrom<IMemorySizedSource>(((EventLogDocument)loaded.Document).Source);
        Assert.True(source.ApproximateMemoryBytes > 0);
        Assert.True(estimate >= source.ApproximateMemoryBytes);
    }
}
