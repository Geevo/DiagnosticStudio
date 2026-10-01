using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Tests.Parsers;

public class LogLineAnalyzerTests
{
    [Theory]
    [InlineData("2026-07-23 14:12:00 ERROR agent failed", LogSeverity.Error)]
    [InlineData("2026-07-23 14:12:00 [WARN] retrying", LogSeverity.Warning)]
    [InlineData("2026-07-23 14:12:00 WARNING: low disk", LogSeverity.Warning)]
    [InlineData("2026-07-23 14:12:00 INFO started", LogSeverity.Information)]
    [InlineData("2026-07-23 14:12:00 DEBUG x=1", LogSeverity.Debug)]
    [InlineData("FATAL: boom", LogSeverity.Error)]
    [InlineData("12:00 [Error] bracketed title case", LogSeverity.Error)]
    [InlineData("12:00 <warning> angle", LogSeverity.Warning)]
    public void Severity_levels_are_recognised(string line, LogSeverity expected)
    {
        Assert.Equal(expected, LogLineAnalyzer.Analyze(line).Severity);
    }

    [Theory]
    [InlineData("No error found in the configuration")]
    [InlineData("Terrorist")]
    [InlineData("INFORMATIONAL text")]
    [InlineData("")]
    public void Ordinary_prose_is_not_a_severity(string line)
    {
        Assert.Equal(LogSeverity.None, LogLineAnalyzer.Analyze(line).Severity);
    }

    [Fact]
    public void Level_tokens_beyond_the_scan_window_are_ignored()
    {
        var line = new string('x', 200) + " ERROR";

        Assert.Equal(LogSeverity.None, LogLineAnalyzer.Analyze(line).Severity);
    }

    [Fact]
    public void Iso_timestamp_is_parsed_and_its_span_reported()
    {
        var info = LogLineAnalyzer.Analyze("[2026-07-23 14:12:00.250] ERROR x");

        Assert.Equal(new DateTime(2026, 7, 23, 14, 12, 0, 250), info.Timestamp);
        Assert.Equal(1, info.TimestampStart);
        Assert.Equal("2026-07-23 14:12:00.250".Length, info.TimestampLength);
    }

    [Fact]
    public void Iso_timestamp_with_offset_is_normalised_to_utc()
    {
        var info = LogLineAnalyzer.Analyze("2026-07-23T14:12:00+02:00 started");

        Assert.Equal(new DateTime(2026, 7, 23, 12, 12, 0), info.Timestamp);
    }

    [Fact]
    public void Slash_dates_are_highlighted_but_not_parsed_because_they_are_ambiguous()
    {
        var info = LogLineAnalyzer.Analyze("07/08/2026 2:12:00 PM something");

        Assert.Null(info.Timestamp);
        Assert.True(info.HasTimestampSpan);
        Assert.Equal(0, info.TimestampStart);
    }

    [Fact]
    public void Cmtrace_lines_use_the_type_attribute_and_date_time_attributes()
    {
        const string line =
            "<![LOG[Install failed]LOG]!><time=\"14:12:00.123+000\" date=\"7-23-2026\" component=\"X\" context=\"\" type=\"3\" thread=\"1\" file=\"\">";

        var info = LogLineAnalyzer.Analyze(line);

        Assert.Equal(LogSeverity.Error, info.Severity);
        Assert.Equal(new DateTime(2026, 7, 23, 14, 12, 0, 123), info.Timestamp);
        Assert.True(info.HasTimestampSpan);
    }

    [Theory]
    [InlineData("1", LogSeverity.Information)]
    [InlineData("2", LogSeverity.Warning)]
    [InlineData("3", LogSeverity.Error)]
    public void Cmtrace_type_maps_to_severity(string type, LogSeverity expected)
    {
        var line = $"<![LOG[msg]LOG]!><time=\"01:02:03.000+000\" date=\"1-2-2026\" type=\"{type}\">";

        Assert.Equal(expected, LogLineAnalyzer.Analyze(line).Severity);
    }

    [Fact]
    public void Plain_text_has_no_timestamp()
    {
        var info = LogLineAnalyzer.Analyze("just some words");

        Assert.False(info.HasTimestampSpan);
        Assert.Null(info.Timestamp);
    }
}
