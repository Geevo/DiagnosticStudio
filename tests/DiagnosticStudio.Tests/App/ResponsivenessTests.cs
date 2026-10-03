using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Findings;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Rules;
using DiagnosticStudio.Rules;
using DiagnosticStudio.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

/// <summary>What keeps the window responsive while a bundle is replaced and background work runs.</summary>
public sealed class ResponsivenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-resp-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();

    public ResponsivenessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- stall detection ----

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Ticks_on_time_are_not_stalls()
    {
        var detector = new StallDetector(Ms(1000), () => TimeSpan.Zero);

        Assert.Null(detector.Tick(Ms(0)));
        Assert.Null(detector.Tick(Ms(100)));
        Assert.Null(detector.Tick(Ms(210)));
        Assert.Null(detector.Tick(Ms(1200)));  // 990 ms gap: under the limit
    }

    [Fact]
    public void A_late_tick_reports_how_long_the_thread_was_busy()
    {
        var detector = new StallDetector(Ms(1000), () => TimeSpan.Zero);
        detector.Tick(Ms(0));
        detector.Tick(Ms(100));

        var stall = detector.Tick(Ms(2600));

        Assert.NotNull(stall);
        Assert.Equal(Ms(2500), stall!.Value.Duration);
    }

    [Fact]
    public void The_first_tick_never_counts_and_a_stall_is_reported_once()
    {
        var detector = new StallDetector(Ms(1000), () => TimeSpan.Zero);

        Assert.Null(detector.Tick(Ms(50_000)));      // nothing to compare with
        Assert.NotNull(detector.Tick(Ms(53_000)));
        Assert.Null(detector.Tick(Ms(53_100)));      // back to normal
    }

    [Fact]
    public void Garbage_collection_time_during_the_stall_is_reported_separately()
    {
        var pause = TimeSpan.Zero;
        var detector = new StallDetector(Ms(1000), () => pause);
        detector.Tick(Ms(0));

        pause += Ms(1800);
        var stall = detector.Tick(Ms(2500));

        Assert.Equal(Ms(1800), stall!.Value.GarbageCollectionPause);

        // The next stall counts only its own pauses.
        pause += Ms(200);
        var next = detector.Tick(Ms(4000));
        Assert.Equal(Ms(200), next!.Value.GarbageCollectionPause);
    }

    [Fact]
    public void The_report_says_how_long_what_was_happening_and_whether_it_was_memory_clean_up()
    {
        var plain = UiResponsivenessMonitor.Describe(new UiStall(Ms(2400), Ms(10)), "Loading big.reg...");
        Assert.Equal("The window did not respond for 2.4 s. At the time: Loading big.reg.", plain);

        var gc = UiResponsivenessMonitor.Describe(new UiStall(Ms(3000), Ms(2200)), "Ready");
        Assert.Contains("memory clean-up held everything for 2.2 s", gc);
        Assert.EndsWith("At the time: Ready.", gc);

        Assert.Contains("nothing the status bar knew of", UiResponsivenessMonitor.Describe(new UiStall(Ms(2000), Ms(0)), " "));
    }

    // ---- the work gate ----

    [Fact]
    public async Task The_gate_lets_only_its_slots_run_at_once_and_gives_them_back()
    {
        var gate = new BackgroundWorkGate(2);
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = await gate.EnterAsync(CancellationToken.None);
        Assert.Equal(2, gate.InUse);

        var third = gate.EnterAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(third.IsCompleted);

        first.Dispose();
        using var acquired = await third;
        Assert.Equal(2, gate.InUse);

        second.Dispose();
        second.Dispose(); // giving a slot back twice does nothing
        Assert.Equal(1, gate.InUse);
    }

    [Fact]
    public async Task Waiting_for_the_gate_can_be_cancelled()
    {
        var gate = new BackgroundWorkGate(1);
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = gate.EnterAsync(cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void A_gate_without_slots_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackgroundWorkGate(0));
    }

    [Fact]
    public async Task Background_services_never_run_more_jobs_than_the_gate_allows()
    {
        var gate = new BackgroundWorkGate(2);
        var running = 0;
        var peak = 0;
        var loader = new ProbeLoader(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
        });
        var files = Enumerable.Range(0, 24).Select(i => Artifact("f" + i + ".log", ArtifactType.TextLog)).ToArray();

        await new FileHealthService(loader, gate).CheckAsync(files, null, CancellationToken.None);
        Assert.True(peak <= 2, "file check ran " + peak + " at once");

        peak = 0;
        await new TimelineService(loader, gate).BuildAsync(files, null, CancellationToken.None);
        Assert.True(peak <= 2, "timeline ran " + peak + " at once");

        peak = 0;
        await new FindingsService(loader, new[] { new AlwaysRule() }, gate).EvaluateAsync(files, null, CancellationToken.None);
        Assert.True(peak <= 2, "rules ran " + peak + " at once");
        Assert.True(peak >= 1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private sealed class ProbeLoader : IDocumentLoader
    {
        private readonly Action _work;

        public ProbeLoader(Action work) => _work = work;

        public Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            _work();
            return Task.FromResult(new DocumentLoadResult(Text(artifact, "2026-07-23 14:12:00 INFO x"), null));
        }
    }

    private sealed class AlwaysRule : IDocumentRule
    {
        public string Id => "always";
        public bool AppliesTo(DiagnosticArtifact artifact) => true;

        public IEnumerable<Finding> Evaluate(DiagnosticArtifact artifact, DiagnosticDocument document, CancellationToken cancellationToken) =>
            Array.Empty<Finding>();
    }

    // ---- replacing a bundle ----

    private sealed class Ingestor : IBundleIngestor
    {
        public Queue<InvestigationWorkspace> Next { get; } = new();

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next.Dequeue());
    }

    private InvestigationWorkspace Workspace(Action cleanup) =>
        new(Guid.NewGuid(), _dir, _dir, new[] { Artifact("a.log", ArtifactType.TextLog) }, Array.Empty<IngestionIssue>(), cleanup);

    [Fact]
    public async Task The_previous_working_folder_is_removed_off_the_calling_thread()
    {
        var cleanedOn = -1;
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var ingestor = new Ingestor();
        var first = Workspace(() =>
        {
            cleanedOn = Environment.CurrentManagedThreadId;
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        ingestor.Next.Enqueue(first);
        ingestor.Next.Enqueue(Workspace(() => { }));
        var vm = new WorkspaceViewModel(ingestor, _output);
        await vm.OpenAsync(_dir);

        // Opening the second returns although removing the first is stuck.
        await vm.OpenAsync(_dir).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(Environment.CurrentManagedThreadId, cleanedOn);
        Assert.False(vm.PendingCleanup.IsCompleted);

        release.Set();
        await vm.PendingCleanup;
    }

    [Fact]
    public async Task Closing_the_bundle_removes_its_folder_in_the_background_too()
    {
        var cleaned = new ManualResetEventSlim();
        var ingestor = new Ingestor();
        ingestor.Next.Enqueue(Workspace(() => cleaned.Set()));
        var vm = new WorkspaceViewModel(ingestor, _output);
        await vm.OpenAsync(_dir);

        vm.Close();
        await vm.PendingCleanup;

        Assert.True(cleaned.IsSet);
        Assert.Null(vm.Current);
    }

    [Fact]
    public async Task Removals_run_one_after_another()
    {
        var order = new List<int>();
        var ingestor = new Ingestor();
        for (var i = 1; i <= 3; i++)
        {
            var n = i;
            ingestor.Next.Enqueue(Workspace(() =>
            {
                lock (order)
                {
                    order.Add(n);
                }

                Thread.Sleep(20);
            }));
        }

        ingestor.Next.Enqueue(Workspace(() => { }));
        var vm = new WorkspaceViewModel(ingestor, _output);
        for (var i = 0; i < 4; i++)
        {
            await vm.OpenAsync(_dir);
        }

        await vm.PendingCleanup;

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public async Task A_removal_that_fails_is_reported_not_thrown()
    {
        var ingestor = new Ingestor();
        ingestor.Next.Enqueue(Workspace(() => throw new InvalidOperationException("locked by a scanner")));
        ingestor.Next.Enqueue(Workspace(() => { }));
        var vm = new WorkspaceViewModel(ingestor, _output);
        await vm.OpenAsync(_dir);
        await vm.OpenAsync(_dir);

        await vm.PendingCleanup;

        Assert.Contains(_output.Entries, e => e.Source == "Housekeeping" && e.Message.Contains("locked by a scanner"));
    }

    [Fact]
    public async Task The_new_workspace_is_in_place_before_the_old_one_has_finished_being_removed()
    {
        var release = new ManualResetEventSlim();
        var ingestor = new Ingestor();
        ingestor.Next.Enqueue(Workspace(() => release.Wait(TimeSpan.FromSeconds(10))));
        var second = Workspace(() => { });
        ingestor.Next.Enqueue(second);
        var vm = new WorkspaceViewModel(ingestor, _output);
        await vm.OpenAsync(_dir);

        await vm.OpenAsync(_dir);

        Assert.Same(second, vm.Current);
        release.Set();
        await vm.PendingCleanup;
    }
}
