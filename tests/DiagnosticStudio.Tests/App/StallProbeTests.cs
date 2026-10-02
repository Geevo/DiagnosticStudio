using System.Diagnostics;
using DiagnosticStudio.App.Services;

namespace DiagnosticStudio.Tests.App;

public sealed class StallProbeTests
{
    private static void Busy(TimeSpan time, bool throwing = false)
    {
        var watch = Stopwatch.StartNew();
        var n = 0;
        while (watch.Elapsed < time)
        {
            n++;
            if (throwing && n % 200 == 0)
            {
                try
                {
                    throw new InvalidOperationException("x");
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    // The probe belongs to the thread that creates it, as the interface thread's does.
    private static T OnOwnThread<T>(Func<T> body)
    {
        T result = default!;
        var thread = new Thread(() => result = body());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void A_beat_on_time_reports_nothing()
    {
        var seen = OnOwnThread(() =>
        {
            using var probe = new StallProbe();
            Thread.Sleep(100);
            return probe.Beat();
        });

        Assert.Null(seen);
    }

    [Fact]
    public void A_busy_interface_thread_is_reported_as_working_with_its_processor_time()
    {
        var seen = OnOwnThread(() =>
        {
            using var probe = new StallProbe();
            probe.Beat();
            Busy(TimeSpan.FromMilliseconds(1800));
            return probe.Beat();
        });

        Assert.NotNull(seen);
        Assert.Contains("interface thread was working", seen);
    }

    [Fact]
    public void A_blocked_interface_thread_is_reported_as_waiting()
    {
        var seen = OnOwnThread(() =>
        {
            using var probe = new StallProbe();
            probe.Beat();
            Thread.Sleep(1800);
            return probe.Beat();
        });

        Assert.NotNull(seen);
        Assert.Contains("mostly waiting", seen);
    }

    [Fact]
    public void Many_exceptions_during_a_stall_are_counted()
    {
        var seen = OnOwnThread(() =>
        {
            using var probe = new StallProbe();
            probe.Beat();
            Busy(TimeSpan.FromMilliseconds(1500), throwing: true);
            return probe.Beat();
        });

        Assert.NotNull(seen);
        Assert.Contains("exceptions were thrown", seen);
    }
}
