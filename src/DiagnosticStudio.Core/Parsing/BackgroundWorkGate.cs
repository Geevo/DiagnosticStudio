namespace DiagnosticStudio.Core.Parsing;

/// <summary>
/// Limits how many background jobs read and parse files at once. Rule evaluation, the file check and the timeline
/// each parse many files in parallel; run together on a modest machine they would take every core and leave the window
/// unable to repaint. A gate shared by all of them keeps some cores free. Work the user asked for (opening a file,
/// searching) does not go through it, so it is never queued behind background work.
/// </summary>
public interface IBackgroundWorkGate
{
    /// <summary>Waits for a free slot. Dispose the result to give the slot back.</summary>
    ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken);
}

public sealed class BackgroundWorkGate : IBackgroundWorkGate
{
    private readonly SemaphoreSlim _slots;

    public BackgroundWorkGate(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        Slots = slots;
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>Half the cores, at least two: the rest stay free for the interface and for what the user opens.</summary>
    public static int DefaultSlots => Math.Max(2, Environment.ProcessorCount / 2);

    public int Slots { get; }

    /// <summary>Slots in use right now.</summary>
    public int InUse => Slots - _slots.CurrentCount;

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_slots);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _slots;

        public Releaser(SemaphoreSlim slots) => _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}

/// <summary>No limit; used where there is nothing else competing, and by tests.</summary>
public sealed class UnlimitedWorkGate : IBackgroundWorkGate
{
    public static UnlimitedWorkGate Instance { get; } = new();

    public ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IDisposable>(NoOp.Instance);

    private sealed class NoOp : IDisposable
    {
        public static NoOp Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
