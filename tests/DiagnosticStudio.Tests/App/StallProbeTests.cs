using System.Diagnostics;
using System.Globalization;
using DiagnosticStudio.App.Services;

namespace DiagnosticStudio.Tests.App;

public sealed class StallProbeTests
{
    private sealed class FakeSource : IStallSource
    {
        public UiThreadState? Ui { get; set; } = new(false, string.Empty, TimeSpan.Zero);

        public TimeSpan ProcessCpuTime { get; set; }

        public UiThreadState? UiThread() => Ui;

        public TimeSpan ProcessCpu() => ProcessCpuTime;
    }

    // Tests running at the same time throw exceptions of their own, which a probe sees too; by default a probe here
    // counts none, and the tests that are about exceptions say which ones are theirs.
    private static (StallProbe Probe, ManualClock Clock, FakeSource Source) Make(Func<Exception, bool>? counts = null)
    {
        var clock = new ManualClock();
        var source = new FakeSource();
        return (new StallProbe(source, clock, counts ?? (_ => false)), clock, source);
    }

    private static string Seconds(double seconds) => string.Create(CultureInfo.CurrentCulture, $"{seconds:0.0}");

    private static void ThrowSome(int count, string message)
    {
        for (var i = 0; i < count; i++)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public void A_beat_on_time_reports_nothing()
    {
        var (probe, clock, _) = Make();
        using var _ = probe;
        probe.Beat();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(probe.Beat());

        // Late, but not yet a stall.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Null(probe.Beat());
    }

    [Fact]
    public void A_busy_interface_thread_is_reported_as_working_with_its_processor_time()
    {
        var (probe, clock, source) = Make();
        using var _ = probe;
        probe.Beat();

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        source.Ui = new UiThreadState(false, string.Empty, TimeSpan.FromSeconds(1.4));
        var seen = probe.Beat();

        Assert.Contains($"the interface thread was working for {Seconds(1.4)} s of it", seen);
    }

    [Fact]
    public void A_blocked_interface_thread_is_reported_as_waiting_and_for_what()
    {
        var (probe, clock, source) = Make();
        using var _ = probe;
        probe.Beat();
        source.Ui = new UiThreadState(true, "UserRequest", TimeSpan.Zero);

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        source.Ui = new UiThreadState(true, "UserRequest", TimeSpan.FromSeconds(0.1));
        var seen = probe.Beat();

        Assert.Contains($"mostly waiting (UserRequest) and worked for {Seconds(0.1)} s", seen);
    }

    [Fact]
    public void A_stall_is_reported_once_when_it_ends()
    {
        var (probe, clock, _) = Make();
        using var _ = probe;
        probe.Beat();

        clock.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.NotNull(probe.Beat());
        Assert.Null(probe.Beat());
    }

    [Fact]
    public void Processor_time_taken_by_other_threads_is_reported()
    {
        var (probe, clock, source) = Make();
        using var _ = probe;
        probe.Beat();

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        source.ProcessCpuTime = TimeSpan.FromSeconds(3);
        var seen = probe.Beat();

        Assert.Contains($"other threads used {Seconds(3)} s of processor time", seen);
    }

    [Fact]
    public void Many_exceptions_during_a_stall_are_counted()
    {
        var (probe, clock, _) = Make(e => e.Message == "many");
        using var _ = probe;
        probe.Beat();

        ThrowSome(30, "many");
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var seen = probe.Beat();

        Assert.Contains("30 exceptions were thrown", seen);
    }

    [Fact]
    public void The_exceptions_are_named_with_the_application_code_that_threw_them()
    {
        var (probe, clock, _) = Make(e => e.Message == "named");
        using var _ = probe;
        probe.Beat();

        ThrowSome(25, "named");
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var seen = probe.Beat();

        Assert.Contains("InvalidOperationException in StallProbeTests.ThrowSome", seen);
    }

    [Fact]
    public void A_few_exceptions_are_not_worth_mentioning()
    {
        var (probe, clock, _) = Make(e => e.Message == "a few");
        using var _ = probe;
        probe.Beat();

        ThrowSome(2, "a few");
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var seen = probe.Beat();

        Assert.DoesNotContain("exceptions", seen ?? string.Empty);
    }

    [Fact]
    public void Exceptions_from_before_the_stall_are_not_counted_in_it()
    {
        var (probe, clock, _) = Make(e => e.Message == "earlier");
        using var _ = probe;
        probe.Beat();
        ThrowSome(30, "earlier");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        probe.Beat();

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var seen = probe.Beat();

        Assert.DoesNotContain("exceptions", seen ?? string.Empty);
    }

    [Fact]
    public void A_disposed_probe_no_longer_listens_for_exceptions()
    {
        var seenByProbe = 0;
        var (probe, _, _) = Make(e =>
        {
            if (e.Message == "listening")
            {
                Interlocked.Increment(ref seenByProbe);
            }

            return false;
        });

        ThrowSome(1, "listening");
        Assert.Equal(1, seenByProbe);

        probe.Dispose();
        ThrowSome(1, "listening");

        Assert.Equal(1, seenByProbe);
    }

    // One run with the real thread and process readings, which the tests above stand in for. The probe samples from
    // the thread pool, which a busy machine running other tests can starve for long enough that it never sees the
    // stall; the test then tries again rather than reporting a probe that works as broken.
    [Fact]
    public void A_busy_interface_thread_is_seen_with_the_real_readings()
    {
        string? seen = null;
        for (var attempt = 0; attempt < 3 && seen is null; attempt++)
        {
            string? result = null;
            var thread = new Thread(() =>
            {
                // The probe belongs to the thread that creates it, as the interface thread's does.
                using var probe = new StallProbe(_ => false);
                probe.Beat();
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 1800)
                {
                }

                result = probe.Beat();
            });
            thread.Start();
            thread.Join();
            seen = result;
        }

        Assert.NotNull(seen);
        Assert.Contains("interface thread was working", seen);
    }
}
