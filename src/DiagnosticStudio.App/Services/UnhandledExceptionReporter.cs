using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Decides what happens when code on the interface thread throws and nothing catches it. An investigation tool must
/// not close in the middle of an investigation over one failed click, so the error is written to Output (with where it
/// happened) and the application carries on. Errors that leave nothing sensible to carry on with (out of memory), and an
/// error that keeps coming back in a loop, are left to end the application as before.
/// </summary>
public sealed class UnhandledExceptionReporter
{
    private readonly IOutputLog _output;
    private readonly Func<DateTime> _now;
    private readonly int _maxInWindow;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _recent = new();

    public UnhandledExceptionReporter(IOutputLog output, Func<DateTime>? now = null, int maxInWindow = 20, TimeSpan? window = null)
    {
        _output = output;
        _now = now ?? (() => DateTime.UtcNow);
        _maxInWindow = maxInWindow;
        _window = window ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Records the error. Returns <c>true</c> when the application should keep running.</summary>
    public bool Report(Exception exception)
    {
        if (exception is OutOfMemoryException or InsufficientExecutionStackException)
        {
            return false;
        }

        var now = _now();
        _recent.Enqueue(now);
        while (_recent.Count > 0 && now - _recent.Peek() > _window)
        {
            _recent.Dequeue();
        }

        if (_recent.Count > _maxInWindow)
        {
            return false; // the same failure over and over: stop rather than spin
        }

        _output.Write(OutputSeverity.Error, "Application", Describe(exception));
        return true;
    }

    internal static string Describe(Exception exception)
    {
        var where = exception.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal));
        var text = $"Something went wrong and the action was skipped: {exception.GetType().Name}: {exception.Message}";
        return where is null ? text : text + " (" + where + ")";
    }
}
