namespace DiagnosticStudio.Tests;

/// <summary>
/// Time that only moves when a test moves it, so nothing that waits depends on how busy the machine is. Timers
/// fire in order as the clock passes their due times, repeating ones as often as they come due.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private TimeSpan _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.Ticks;

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (_timers.Where(t => t.IsDueBy(target)).OrderBy(t => t.Due).FirstOrDefault() is { } next)
        {
            _now = next.Due;
            next.Fire();
        }

        _now = target;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public TimeSpan Due { get; private set; }

        private bool Active { get; set; }

        public bool IsDueBy(TimeSpan time) => Active && Due <= time;

        public void Fire()
        {
            if (_period > TimeSpan.Zero)
            {
                Due += _period;
            }
            else
            {
                Active = false;
            }

            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Active = dueTime != Timeout.InfiniteTimeSpan;
            Due = _clock._now + dueTime;
            _period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            return true;
        }

        public void Dispose()
        {
            Active = false;
            _clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
