using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Tests.Core;

public sealed class CachingDocumentLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-cache-" + Guid.NewGuid().ToString("N"));

    public CachingDocumentLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Counts parses and can hold them open so tests control overlap and cancellation.</summary>
    private sealed class FakeInner : IDocumentLoader
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int Cancelled;
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Throw { get; set; }
        public string? FailureMessage { get; set; }

        public async Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is { } gate)
            {
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Cancelled);
                    throw;
                }
            }

            if (Throw is { } ex)
            {
                throw ex;
            }

            return new DocumentLoadResult(new UnsupportedDocument { Artifact = artifact, Reason = "test" }, FailureMessage);
        }
    }

    private DiagnosticArtifact Artifact(string name = "a.log", long size = 100)
    {
        var path = Path.Combine(_dir, name + "-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "content");
        return new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { name },
            ArtifactType = ArtifactType.TextLog,
            Size = size,
        };
    }

    // Artifact.Size doubles as the test weight.
    private static CachingDocumentLoader Cache(FakeInner inner, long budget = 1000) =>
        new(inner, new DocumentCacheOptions(budget, (a, _) => a.Size));

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition was not reached in time");
    }

    // ---- sharing ----

    [Fact]
    public async Task A_second_request_reuses_the_parsed_document()
    {
        var inner = new FakeInner();
        var cache = Cache(inner);
        var artifact = Artifact();

        var first = await cache.LoadAsync(artifact, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 1 && cache.Statistics.EstimatedBytes > 0);
        var second = await cache.LoadAsync(artifact, CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Same(first.Document, second.Document);
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.Equal(1, cache.Statistics.Misses);
    }

    [Fact]
    public async Task Concurrent_requests_share_a_single_parse()
    {
        var inner = new FakeInner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(inner);
        var artifact = Artifact();

        var loads = Enumerable.Range(0, 8).Select(_ => cache.LoadAsync(artifact, CancellationToken.None)).ToArray();
        await Eventually(() => inner.Calls == 1);
        inner.Gate.SetResult();
        var results = await Task.WhenAll(loads);

        Assert.Equal(1, inner.Calls);
        Assert.All(results, r => Assert.Same(results[0].Document, r.Document));
    }

    [Fact]
    public async Task Different_artifacts_are_cached_independently()
    {
        var inner = new FakeInner();
        var cache = Cache(inner);
        var a = Artifact("a.log");
        var b = Artifact("b.log");

        var da = (await cache.LoadAsync(a, CancellationToken.None)).Document;
        var db = (await cache.LoadAsync(b, CancellationToken.None)).Document;

        Assert.Equal(2, inner.Calls);
        Assert.NotSame(da, db);
        Assert.Equal(a.Id, da.Artifact.Id);
    }

    [Fact]
    public async Task A_parser_failure_message_is_kept_with_the_cached_result()
    {
        var inner = new FakeInner { FailureMessage = "parser said no" };
        var cache = Cache(inner);
        var artifact = Artifact();

        await cache.LoadAsync(artifact, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 1);
        var again = await cache.LoadAsync(artifact, CancellationToken.None);

        Assert.Equal("parser said no", again.FailureMessage);
        Assert.Equal(1, inner.Calls);
    }

    // ---- budget ----

    [Fact]
    public async Task The_least_recently_used_document_is_evicted_when_the_budget_is_exceeded()
    {
        var inner = new FakeInner();
        var cache = Cache(inner, budget: 250);
        var a = Artifact("a", size: 100);
        var b = Artifact("b", size: 100);
        var c = Artifact("c", size: 100);

        await cache.LoadAsync(a, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 1);
        await cache.LoadAsync(b, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 2);
        await cache.LoadAsync(a, CancellationToken.None); // a is now more recent than b
        await cache.LoadAsync(c, CancellationToken.None);
        await Eventually(() => cache.Statistics.Evictions == 1);

        Assert.Equal(2, cache.Statistics.Entries);
        Assert.True(cache.Statistics.EstimatedBytes <= 250);
        var callsBefore = inner.Calls;
        await cache.LoadAsync(a, CancellationToken.None); // kept
        Assert.Equal(callsBefore, inner.Calls);
        await cache.LoadAsync(b, CancellationToken.None); // evicted, so parsed again
        Assert.Equal(callsBefore + 1, inner.Calls);
    }

    [Fact]
    public async Task A_document_bigger_than_the_whole_budget_is_returned_but_not_kept()
    {
        var inner = new FakeInner();
        var cache = Cache(inner, budget: 100);
        var huge = Artifact("huge", size: 5000);

        var first = await cache.LoadAsync(huge, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 0);
        var second = await cache.LoadAsync(huge, CancellationToken.None);

        Assert.NotNull(first.Document);
        Assert.Equal(2, inner.Calls);
        Assert.NotSame(first.Document, second.Document);
        Assert.Equal(0, cache.Statistics.EstimatedBytes);
    }

    [Fact]
    public async Task Clear_empties_the_cache()
    {
        var inner = new FakeInner();
        var cache = Cache(inner);
        var artifact = Artifact();
        await cache.LoadAsync(artifact, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 1);

        cache.Clear();

        Assert.Equal(0, cache.Statistics.Entries);
        Assert.Equal(0, cache.Statistics.EstimatedBytes);
        await cache.LoadAsync(artifact, CancellationToken.None);
        Assert.Equal(2, inner.Calls);
    }

    // ---- staleness ----

    [Fact]
    public async Task A_file_that_changes_is_parsed_again()
    {
        var inner = new FakeInner();
        var cache = Cache(inner);
        var artifact = Artifact();
        var first = await cache.LoadAsync(artifact, CancellationToken.None);
        await Eventually(() => cache.Statistics.Entries == 1);

        await File.WriteAllTextAsync(artifact.ExtractedPath!, "content that is now longer than before");
        var second = await cache.LoadAsync(artifact, CancellationToken.None);

        Assert.Equal(2, inner.Calls);
        Assert.NotSame(first.Document, second.Document);
    }

    [Fact]
    public async Task A_missing_file_is_still_handed_to_the_parser_to_report()
    {
        var inner = new FakeInner();
        var cache = Cache(inner);
        var artifact = Artifact() with { ExtractedPath = Path.Combine(_dir, "does-not-exist.log") };

        var result = await cache.LoadAsync(artifact, CancellationToken.None);

        Assert.NotNull(result.Document);
        Assert.Equal(1, inner.Calls);
    }

    // ---- failure and cancellation ----

    [Fact]
    public async Task A_failed_load_is_not_cached_and_is_retried()
    {
        var inner = new FakeInner { Throw = new InvalidOperationException("boom") };
        var cache = Cache(inner);
        var artifact = Artifact();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.LoadAsync(artifact, CancellationToken.None));
        await Eventually(() => cache.Statistics.Entries == 0);

        inner.Throw = null;
        var ok = await cache.LoadAsync(artifact, CancellationToken.None);

        Assert.NotNull(ok.Document);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_cancel_a_load_another_still_wants()
    {
        var inner = new FakeInner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(inner);
        var artifact = Artifact();
        using var impatient = new CancellationTokenSource();

        var patient = cache.LoadAsync(artifact, CancellationToken.None);
        var quitter = cache.LoadAsync(artifact, impatient.Token);
        await Eventually(() => inner.Calls == 1);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quitter);

        inner.Gate.SetResult();
        var result = await patient;

        Assert.NotNull(result.Document);
        Assert.Equal(0, inner.Cancelled);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task When_every_caller_gives_up_the_parse_is_cancelled_and_the_next_request_starts_afresh()
    {
        var inner = new FakeInner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(inner);
        var artifact = Artifact();
        using var cts = new CancellationTokenSource();

        var only = cache.LoadAsync(artifact, cts.Token);
        await Eventually(() => inner.Calls == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        await Eventually(() => inner.Cancelled == 1);

        inner.Gate = null;
        var next = await cache.LoadAsync(artifact, CancellationToken.None);

        Assert.NotNull(next.Document);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_request_with_an_already_cancelled_token_does_no_work()
    {
        var inner = new FakeInner();
        var cache = Cache(inner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.LoadAsync(Artifact(), cts.Token));

        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Clearing_while_a_load_is_running_cancels_it_and_leaves_nothing_cached()
    {
        var inner = new FakeInner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = Cache(inner);
        var pending = cache.LoadAsync(Artifact(), CancellationToken.None);
        await Eventually(() => inner.Calls == 1);

        cache.Clear();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, cache.Statistics.Entries);
    }

    // ---- stress ----

    [Fact]
    public async Task Heavy_concurrent_use_stays_within_budget_and_returns_the_right_document()
    {
        var inner = new FakeInner();
        var cache = Cache(inner, budget: 500);
        var artifacts = Enumerable.Range(0, 20).Select(i => Artifact("f" + i, size: 100)).ToList();

        await Task.WhenAll(Enumerable.Range(0, 400).Select(async i =>
        {
            var artifact = artifacts[i * 7 % artifacts.Count];
            var result = await cache.LoadAsync(artifact, CancellationToken.None);
            Assert.Equal(artifact.Id, result.Document.Artifact.Id);
        }));

        await Eventually(() => cache.Statistics.EstimatedBytes <= 500);
        Assert.InRange(cache.Statistics.Entries, 0, 5);
        Assert.True(inner.Calls >= 20);
    }
}
