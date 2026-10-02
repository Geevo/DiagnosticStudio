using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.App.Services;

/// <summary>One period in which the interface thread did not run.</summary>
/// <param name="Duration">How long the window was unresponsive.</param>
/// <param name="GarbageCollectionPause">How much of that the .NET garbage collector had everything stopped.</param>
public readonly record struct UiStall(TimeSpan Duration, TimeSpan GarbageCollectionPause);

/// <summary>
/// Notices when a regular tick on the interface thread arrives late, which means the thread was busy and the window
/// could not repaint or take input. Separate from the timer so it can be tested without one.
/// </summary>
public sealed class StallDetector
{
    private readonly TimeSpan _threshold;
    private readonly Func<TimeSpan> _gcPause;
    private TimeSpan _lastTick;
    private TimeSpan _lastGcPause;
    private bool _started;

    /// <param name="threshold">A gap longer than this between ticks is a stall.</param>
    /// <param name="gcPause">Total time the garbage collector has paused the process so far.</param>
    public StallDetector(TimeSpan threshold, Func<TimeSpan> gcPause)
    {
        _threshold = threshold;
        _gcPause = gcPause;
    }

    /// <summary>Call on every tick with the current time. Returns the stall that just ended, if there was one.</summary>
    public UiStall? Tick(TimeSpan now)
    {
        var gc = _gcPause();
        UiStall? stall = null;
        if (_started && now - _lastTick > _threshold)
        {
            stall = new UiStall(now - _lastTick, gc - _lastGcPause);
        }

        _started = true;
        _lastTick = now;
        _lastGcPause = gc;
        return stall;
    }
}

/// <summary>
/// Writes a line to the Output panel whenever the window freezes for more than a moment, with what the application
/// was doing and how much of the time was garbage collection. A freeze that goes unexplained cannot be fixed; this
/// makes the next report carry the evidence.
/// </summary>
public sealed class UiResponsivenessMonitor : IDisposable
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _timer;
    private readonly StallDetector _detector;
    private readonly IOutputLog _output;
    private readonly Func<string> _activity;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StallProbe _probe = new();

    public UiResponsivenessMonitor(Dispatcher dispatcher, IOutputLog output, Func<string> activity, TimeSpan? threshold = null)
    {
        _output = output;
        _activity = activity;
        _detector = new StallDetector(threshold ?? DefaultThreshold, GC.GetTotalPauseDuration);
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TickInterval };
        _timer.Tick += (_, _) =>
        {
            var seen = _probe.Beat();
            Report(_detector.Tick(_clock.Elapsed), seen);
        };
    }

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _probe.Dispose();
    }

    private void Report(UiStall? stall, string? seen)
    {
        if (stall is not { } s)
        {
            return;
        }

        var text = Describe(s, _activity());
        _output.Write(OutputSeverity.Warning, "Performance", string.IsNullOrEmpty(seen) ? text : text + " " + seen);
    }

    internal static string Describe(UiStall stall, string activity)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = string.Create(culture, $"The window did not respond for {stall.Duration.TotalSeconds:0.0} s");
        if (stall.GarbageCollectionPause > TimeSpan.FromMilliseconds(100))
        {
            text += string.Create(culture, $" (memory clean-up held everything for {stall.GarbageCollectionPause.TotalSeconds:0.0} s of it)");
        }

        return text + ". At the time: " + (string.IsNullOrWhiteSpace(activity) ? "nothing the status bar knew of" : activity.Trim('.', ' ')) + ".";
    }
}
