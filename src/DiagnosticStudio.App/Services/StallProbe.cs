using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Works out what the interface thread was doing while the window was frozen, which the freeze message alone cannot
/// say. A separate thread watches the heartbeat the interface thread gives; once it is late it samples that thread's
/// state, and when the heartbeat returns it reports what it saw: running (so something on that thread was busy),
/// waiting (and what for: a disk page, a lock, another thread), how many exceptions were thrown meanwhile and by
/// what (each one costs far more with a debugger attached, which stops every thread while it handles one), and how
/// much processor time the other threads took.
/// </summary>
public sealed class StallProbe : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly long LateAfterTicks = Stopwatch.Frequency * 7 / 10;
    private static readonly ConcurrentDictionary<Assembly, bool> OwnAssemblies = new();

    // The most recent exceptions, with when and where. A stall is described from the ones inside it.
    private const int RingSize = 4096;

    // Past this many, exceptions are only counted: working out where one came from is not free.
    private const int RecordLimit = 200_000;

    private sealed record Thrown(long Ticks, string Site);

    private readonly uint _uiThreadId = GetCurrentThreadId();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly Dictionary<string, int> _waits = new(StringComparer.Ordinal);
    private readonly Thrown?[] _ring = new Thrown?[RingSize];
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
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => Record(e.Exception);
        _timer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>Called on every interface-thread tick. Returns a description of the stall that just ended, or <c>null</c>.</summary>
    public string? Beat()
    {
        lock (_gate)
        {
            var stallStart = _lastBeat;
            _lastBeat = Stopwatch.GetTimestamp();
            if (!_inStall)
            {
                return null;
            }

            _inStall = false;
            return Describe(stallStart);
        }
    }

    // Runs on the thread that threw, so the stack is the one that matters.
    private void Record(Exception exception)
    {
        var count = Interlocked.Increment(ref _exceptions);
        var site = count <= RecordLimit ? SiteOf(exception) : exception.GetType().Name;
        _ring[(int)((count - 1) % RingSize)] = new Thrown(Stopwatch.GetTimestamp(), site);
    }

    /// <summary>The exception type and the application code that was running when it was thrown.</summary>
    private static string SiteOf(Exception exception)
    {
        var type = exception.GetType().Name;
        try
        {
            MethodBase? first = null;
            foreach (var frame in new StackTrace(skipFrames: 1, fNeedFileInfo: false).GetFrames())
            {
                if (frame.GetMethod() is not { DeclaringType: { } declaring } method)
                {
                    continue;
                }

                // The probe's own frames (this method, the handler) sit above the code that threw.
                if (declaring == typeof(StallProbe) || declaring.DeclaringType == typeof(StallProbe))
                {
                    continue;
                }

                first ??= method;
                if (OwnAssemblies.GetOrAdd(declaring.Assembly, a => a.GetName().Name?.StartsWith("DiagnosticStudio", StringComparison.Ordinal) == true))
                {
                    return $"{type} in {Name(method)}";
                }
            }

            return first is null ? type : $"{type} in {Name(first)}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return type;
        }
    }

    // Lambdas and async methods live in compiler-made types and methods; show the name of the method they belong to.
    private static string Name(MethodBase method)
    {
        static string Plain(string name) => name.StartsWith('<') && name.IndexOf('>') is var end and > 1 ? name[1..end] : name;

        var type = method.DeclaringType!;
        var name = method.Name == "MoveNext" ? Plain(type.Name) : Plain(method.Name);
        while (type is { IsNested: true, DeclaringType: { } outer } && type.Name.StartsWith('<'))
        {
            type = outer;
        }

        return $"{type.Name}.{name}";
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

    private string Describe(long stallStart)
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

        var thrown = _ring.Where(t => t is not null && t.Ticks >= stallStart).Select(t => t!).ToList();
        var exceptions = Math.Max(thrown.Count, Interlocked.Read(ref _exceptions) - _exceptionsAtStart);

        // A handful is nothing, unless a debugger is attached: it stops every thread for each one.
        if (exceptions >= (Debugger.IsAttached ? 3 : 20))
        {
            var top = thrown.GroupBy(t => t.Site).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => string.Create(culture, $"{g.Count():N0} x {g.Key}"));
            var where = thrown.Count == 0 ? string.Empty : $" (mostly {string.Join("; ", top)})";
            parts.Add(string.Create(culture, $"{exceptions:N0} exceptions were thrown") + where
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
