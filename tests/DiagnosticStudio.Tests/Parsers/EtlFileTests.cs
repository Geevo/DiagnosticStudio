using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Parsers.Etl;

namespace DiagnosticStudio.Tests.Parsers;

public sealed class EtlFileTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Parsers", "Fixtures", "small-trace.etl");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-etl-" + Guid.NewGuid().ToString("N"));

    public EtlFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ======================= reading a real trace =======================

    [Fact]
    public void A_trace_is_read_event_by_event_in_time_order()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None);

        Assert.Equal(4, etl.Count);
        Assert.False(etl.IsTruncated);
        var times = Enumerable.Range(0, etl.Count).Select(i => etl.GetSummary(i).TimeUtc).ToArray();
        Assert.Equal(times.OrderBy(t => t), times);
        Assert.All(times, t => Assert.InRange(t.Year, 2020, 2100));
    }

    [Fact]
    public void Events_are_numbered_from_one_and_found_by_number()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None);

        Assert.Equal(1, etl.GetSummary(0).RecordId);
        Assert.Equal(4, etl.GetSummary(3).RecordId);
        Assert.Equal(2, etl.FindByRecordId(3));
        Assert.Equal(-1, etl.FindByRecordId(0));
        Assert.Equal(-1, etl.FindByRecordId(5));
    }

    [Fact]
    public void Providers_are_listed_with_names_and_counts()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None);

        Assert.Equal(2, etl.Providers.Count);
        Assert.Equal(4, etl.Providers.Sum(p => p.Count));
        var runtime = Assert.Single(etl.Providers, p => p.Name == "Microsoft-Windows-DotNETRuntime");
        Assert.Equal(2, runtime.Count);
        Assert.Equal("Microsoft-Windows-DotNETRuntime", etl.ProviderName(runtime.Id));
    }

    [Fact]
    public void An_event_is_decoded_into_named_values_with_a_message_and_xml()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None);

        var detail = etl.ReadDetail(2);

        Assert.Equal("Microsoft-Windows-DotNETRuntime", detail.Provider);
        Assert.NotNull(detail.ProviderGuid);
        Assert.Contains(detail.Data, d => d.Name == "ClrInstanceID");
        Assert.NotNull(detail.ProcessId);
        Assert.Contains("ClrInstanceID", detail.Message);
        Assert.Null(detail.DecodeError);
        Assert.StartsWith("<Event>", detail.Xml);
        Assert.Contains("<Data Name=\"ClrInstanceID\">", detail.Xml);
        System.Xml.Linq.XDocument.Parse(detail.Xml); // always well-formed
    }

    [Fact]
    public void The_two_header_events_are_shown_as_trace_information()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None);

        var header = etl.ReadDetail(0);

        Assert.Contains(header.Data, d => d.Name == "NumberOfProcessors");
    }

    [Fact]
    public void Search_text_holds_the_provider_the_names_and_the_values()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None);

        var text = etl.ReadSearchText(2, includeMessage: false);

        Assert.Contains("DotNETRuntime", text);
        Assert.Contains("ClrInstanceID", text);
    }

    [Fact]
    public void Only_the_first_events_are_kept_when_the_limit_is_reached_and_that_is_said()
    {
        var etl = EtlFile.Open(Fixture, CancellationToken.None, maxEvents: 2);

        Assert.Equal(2, etl.Count);
        Assert.True(etl.IsTruncated);
        Assert.Contains(etl.Notes, n => n.Contains("Only the first 2 are shown"));
    }

    [Fact]
    public void Reading_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => EtlFile.Open(Fixture, cts.Token));
    }

    [Fact]
    public async Task The_trace_stays_readable_while_another_copy_of_it_is_open()
    {
        var both = await Task.WhenAll(
            EtlFile.OpenAsync(Fixture, CancellationToken.None),
            EtlFile.OpenAsync(Fixture, CancellationToken.None));

        Assert.All(both, e => Assert.Equal(4, e.Count));
    }

    // ======================= files that are not traces =======================

    [Fact]
    public void A_file_that_is_not_a_trace_is_reported_not_crashed_on()
    {
        var path = Write("not-a-trace.etl", Encoding.UTF8.GetBytes("This is just some text, not a trace log."));

        Assert.Throws<InvalidDataException>(() => EtlFile.Open(path, CancellationToken.None));
    }

    [Fact]
    public void An_empty_file_is_reported()
    {
        var path = Write("empty.etl", Array.Empty<byte>());

        Assert.Throws<InvalidDataException>(() => EtlFile.Open(path, CancellationToken.None));
    }

    [Fact]
    public void A_missing_file_is_reported()
    {
        Assert.Throws<InvalidDataException>(() => EtlFile.Open(Path.Combine(_dir, "missing.etl"), CancellationToken.None));
    }

    [Fact]
    public void A_trace_cut_short_still_gives_the_events_that_are_there_or_a_clear_failure()
    {
        var bytes = File.ReadAllBytes(Fixture);
        foreach (var keep in new[] { 100, 4096, 20_000, bytes.Length - 1 })
        {
            var path = Write("cut-" + keep + ".etl", bytes[..keep]);

            try
            {
                var etl = EtlFile.Open(path, CancellationToken.None);
                Assert.True(etl.Count <= 4);
            }
            catch (InvalidDataException)
            {
                // Acceptable: nothing could be read.
            }
        }
    }

    [Fact]
    public void A_damaged_trace_never_crashes_the_reader()
    {
        var original = File.ReadAllBytes(Fixture);
        var random = new Random(1234);
        for (var round = 0; round < 25; round++)
        {
            var bytes = (byte[])original.Clone();
            for (var i = 0; i < 40; i++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            var path = Write("damaged-" + round + ".etl", bytes);
            try
            {
                var etl = EtlFile.Open(path, CancellationToken.None);
                for (var i = 0; i < etl.Count; i++)
                {
                    _ = etl.ReadDetail(i);
                    _ = etl.ReadSearchText(i, includeMessage: true);
                }
            }
            catch (InvalidDataException)
            {
                // Acceptable.
            }
        }
    }

    // ======================= the parser =======================

    private static DiagnosticArtifact TraceArtifact(string path, ArtifactType type = ArtifactType.Trace) => new()
    {
        Id = Guid.NewGuid(),
        Name = Path.GetFileName(path),
        OriginalPath = Path.GetFileName(path),
        ExtractedPath = path,
        Provenance = new[] { "Bundle", Path.GetFileName(path) },
        ArtifactType = type,
    };

    [Fact]
    public async Task The_parser_opens_a_trace_as_an_event_log()
    {
        var parser = new EtlParser();
        var artifact = TraceArtifact(Fixture);

        Assert.True(parser.CanHandle(artifact));
        var document = Assert.IsType<EventLogDocument>(await parser.ParseAsync(artifact, CancellationToken.None));

        Assert.Equal(4, document.Source.Count);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void The_parser_takes_only_traces_that_have_a_file()
    {
        var parser = new EtlParser();
        var artifact = TraceArtifact(Fixture);

        Assert.False(parser.CanHandle(artifact with { ArtifactType = ArtifactType.EventLog }));
        Assert.False(parser.CanHandle(artifact with { ExtractedPath = null }));
    }

    [Fact]
    public async Task A_file_that_is_not_a_trace_fails_the_parse_with_a_reason()
    {
        var path = Write("bad.etl", Encoding.UTF8.GetBytes("nope"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new EtlParser().ParseAsync(TraceArtifact(path), CancellationToken.None));
    }

    // ======================= pieces =======================

    [Fact]
    public void Numbered_places_in_a_message_are_filled_from_the_values()
    {
        var text = EtlEventDecoder.Substitute("Process %1 exited with code %2 (%3)", new[] { "a.exe", "5" });

        Assert.Equal("Process a.exe exited with code 5 (%3)", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_message_text_gives_no_message(string? template)
    {
        Assert.Null(EtlEventDecoder.Substitute(template, new[] { "x" }));
    }

    [Fact]
    public void A_doubled_percent_sign_in_a_message_is_one()
    {
        Assert.Equal("100% done", EtlEventDecoder.Substitute("100%% done", Array.Empty<string>()));
    }

    [Fact]
    public void Readable_text_is_found_in_raw_payloads_in_both_ascii_and_utf16()
    {
        var payload = new byte[] { 1, 2, 3, 0 }
            .Concat(Encoding.ASCII.GetBytes("Starting service"))
            .Concat(new byte[] { 0, 0xFF })
            .Concat(Encoding.Unicode.GetBytes("C:\\Windows\\Temp"))
            .Concat(new byte[] { 0, 0, 9 })
            .ToArray();

        var text = EtlEventDecoder.ReadableStrings(payload);

        Assert.Contains("Starting service", text);
        Assert.Contains("C:\\Windows\\Temp", text);
    }

    [Fact]
    public void Short_or_binary_payloads_give_no_text()
    {
        Assert.Equal(string.Empty, EtlEventDecoder.ReadableStrings(new byte[] { 1, 2, 3, 4, 0, 0, 0, 255, 128, 7 }));
        Assert.Equal(string.Empty, EtlEventDecoder.ReadableStrings(Array.Empty<byte>()));
    }
}
