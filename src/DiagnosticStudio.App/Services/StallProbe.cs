using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Works out what the interface thread was doing while the window was frozen, which the freeze message alone cannot
/// say. A separate thread watches the heartbeat the interface thread gives; once it is late it samples that thread's
/// state, and when the heartbeat returns it reports what it saw: running (so something on that thread was busy),
/// waiting (and what for: a disk page, a lock, another thread), how many exceptions were thrown meanwhile (each one
/// costs far more with a debugger attached), and how much processor time the other threads took.
/// </summary>
public sealed class StallProbe : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly long LateAfterTicks = Stopwatch.Frequency * 7 / 10;

    private readonly uint _uiThreadId = GetCurrentThreadId();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly Dictionary<string, int> _waits = new(StringComparer.Ordinal);
    private long _lastBeat = Stopwatch.GetTimestamp();
    private long _exceptions;
    private bool _inStall;
    private int _samples;
    private int _runningSamples;
    private long _exceptionsAtStart;
    private TimeSpan _uiCpuAtStart;
    private TimeSpan _processCpuAtStart;

    public StallProbe()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, _) => Interlocked.Increment(ref _exceptions);
        _timer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>Called on every interface-thread tick. Returns a description of the stall that just ended, or <c>null</c>.</summary>
    public string? Beat()
    {
        lock (_gate)
        {
            _lastBeat = Stopwatch.GetTimestamp();
            if (!_inStall)
            {
                return null;
            }

            _inStall = false;
            return Describe();
        }
    }

    private void Sample()
    {
        try
        {
            lock (_gate)
            {
                if (Stopwatch.GetTimestamp() - _lastBeat < LateAfterTicks)
                {
                    return;
                }

                using var process = Process.GetCurrentProcess();
                var ui = FindUiThread(process);
                if (!_inStall)
                {
                    _inStall = true;
                    _samples = 0;
                    _runningSamples = 0;
                    _waits.Clear();
                    _exceptionsAtStart = Interlocked.Read(ref _exceptions);
                    _uiCpuAtStart = ui is null ? TimeSpan.Zero : SafeCpu(ui);
                    _processCpuAtStart = process.TotalProcessorTime;
                }

                _samples++;
                if (ui is null)
                {
                    return;
                }

                if (ui.ThreadState == System.Diagnostics.ThreadState.Wait)
                {
                    var reason = ui.WaitReason.ToString();
                    _waits[reason] = _waits.GetValueOrDefault(reason) + 1;
                }
                else
                {
                    _runningSamples++;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // A sample that cannot be taken is simply missing from the report.
        }
    }

    private ProcessThread? FindUiThread(Process process)
    {
        foreach (ProcessThread thread in process.Threads)
        {
            if ((uint)thread.Id == _uiThreadId)
            {
                return thread;
            }
        }

        return null;
    }

    private static TimeSpan SafeCpu(ProcessThread thread)
    {
        try
        {
            return thread.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private string Describe()
    {
        var culture = CultureInfo.CurrentCulture;
        var parts = new List<string>();
        using var process = Process.GetCurrentProcess();
        var ui = FindUiThread(process);
        if (ui is not null && _samples > 0)
        {
            var uiCpu = SafeCpu(ui) - _uiCpuAtStart;
            if (_runningSamples * 2 >= _samples)
            {
                parts.Add(string.Create(culture, $"the interface thread was working for {uiCpu.TotalSeconds:0.0} s of it"));
            }
            else
            {
                var top = _waits.OrderByDescending(w => w.Value).Take(2).Select(w => w.Key);
                parts.Add(string.Create(culture, $"the interface thread was mostly waiting ({string.Join(", ", top)}) and worked for {uiCpu.TotalSeconds:0.0} s"));
            }
        }

        var exceptions = Interlocked.Read(ref _exceptions) - _exceptionsAtStart;
        if (exceptions > 50)
        {
            parts.Add(string.Create(culture, $"{exceptions:N0} exceptions were thrown")
                + (Debugger.IsAttached ? " with a debugger attached, which makes each one slow" : string.Empty));
        }

        var others = process.TotalProcessorTime - _processCpuAtStart - (ui is null ? TimeSpan.Zero : SafeCpu(ui) - _uiCpuAtStart);
        if (others > TimeSpan.FromSeconds(1))
        {
            parts.Add(string.Create(culture, $"other threads used {others.TotalSeconds:0.0} s of processor time"));
        }

        return parts.Count == 0 ? string.Empty : "Seen: " + string.Join("; ", parts) + ".";
    }

    public void Dispose() => _timer.Dispose();
}
