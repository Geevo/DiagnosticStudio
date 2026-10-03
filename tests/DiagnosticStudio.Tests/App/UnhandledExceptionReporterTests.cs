using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.Tests.App;

public class UnhandledExceptionReporterTests
{
    private readonly OutputViewModel _output = new();

    [Fact]
    public void An_error_is_written_to_output_and_the_application_carries_on()
    {
        var reporter = new UnhandledExceptionReporter(_output);

        var carryOn = reporter.Report(new InvalidOperationException("boom"));

        Assert.True(carryOn);
        var entry = Assert.Single(_output.Entries);
        Assert.Equal(OutputSeverity.Error, entry.Severity);
        Assert.Equal("Application", entry.Source);
        Assert.Contains("InvalidOperationException", entry.Message);
        Assert.Contains("boom", entry.Message);
    }

    [Fact]
    public void Where_it_happened_is_included_when_there_is_a_stack()
    {
        var reporter = new UnhandledExceptionReporter(_output);
        Exception? thrown = null;
        try
        {
            throw new InvalidOperationException("x");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        reporter.Report(thrown!);

        Assert.Contains(nameof(Where_it_happened_is_included_when_there_is_a_stack), _output.Entries[0].Message);
    }

    [Fact]
    public void Running_out_of_memory_is_not_swallowed()
    {
        var reporter = new UnhandledExceptionReporter(_output);

        Assert.False(reporter.Report(new OutOfMemoryException()));
        Assert.False(reporter.Report(new InsufficientExecutionStackException()));
        Assert.Empty(_output.Entries);
    }

    [Fact]
    public void The_same_failure_in_a_loop_is_allowed_to_end_the_application()
    {
        var now = new DateTime(2026, 7, 23, 14, 0, 0, DateTimeKind.Utc);
        var reporter = new UnhandledExceptionReporter(_output, () => now, maxInWindow: 5, window: TimeSpan.FromSeconds(5));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(reporter.Report(new InvalidOperationException("again")));
        }

        Assert.False(reporter.Report(new InvalidOperationException("again")));
        Assert.Equal(5, _output.Entries.Count); // the one that gave up is not logged
    }

    [Fact]
    public void Occasional_errors_spread_over_time_never_add_up_to_a_loop()
    {
        var now = new DateTime(2026, 7, 23, 14, 0, 0, DateTimeKind.Utc);
        var reporter = new UnhandledExceptionReporter(_output, () => now, maxInWindow: 3, window: TimeSpan.FromSeconds(5));

        for (var i = 0; i < 20; i++)
        {
            now = now.AddSeconds(10);
            Assert.True(reporter.Report(new InvalidOperationException("now and then")));
        }

        Assert.Equal(20, _output.Entries.Count);
    }
}
