using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Parsers.Evtx;

namespace DiagnosticStudio.Tests.Evtx;

public sealed class EvtxFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-evtx-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);

    public EvtxFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Save(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".evtx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static List<TestEvent> Events(int count, long firstId = 1) =>
        Enumerable.Range(0, count)
            .Select(i => new TestEvent(
                firstId + i,
                i % 3 == 0 ? "Service Control Manager" : i % 3 == 1 ? "Schannel" : "Application Error",
                (uint)(7000 + (i % 40)),
                (byte)(1 + (i % 4)),
                T0.AddSeconds(i),
                "PC-02341",
                $"param one {i}",
                $"second <&> \"{i}\""))
            .ToList();

    private static Task<EvtxFile> Open(string path, IEventMessageFormatter? formatter = null) =>
        EvtxFile.OpenAsync(path, formatter, CancellationToken.None);

    // ---- indexing ----

    [Fact]
    public async Task Indexes_events_with_their_header_facts()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Events(10))));

        Assert.Equal(10, file.Count);
        Assert.Equal("3.1", file.FormatVersion);
        Assert.False(file.IsDirty);
        Assert.Equal(0, file.TotalIssueCount);

        var s = file.GetSummary(4);
        Assert.Equal(5, s.RecordId);
        Assert.Equal(7004u, s.EventId);
        Assert.Equal(T0.AddSeconds(4), s.TimeUtc);
        Assert.Equal(EventLevels.Critical, s.Level);
        Assert.Equal("Schannel", file.ProviderName(s.ProviderId));
    }

    [Fact]
    public async Task Providers_are_counted_and_listed_most_frequent_first()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Events(10))));

        Assert.Equal(("Service Control Manager", 4), (file.Providers[0].Name, file.Providers[0].Count));
        Assert.Equal(new[] { 3, 3 }, file.Providers.Skip(1).Select(p => p.Count));
        Assert.Equal("Service Control Manager", file.Providers[0].Name);
        Assert.Equal(10, file.Providers.Sum(p => p.Count));
    }

    [Fact]
    public async Task Events_spanning_many_chunks_are_all_found_and_decode_correctly()
    {
        var builder = new EvtxBuilder();
        var events = Events(2500);
        var file = await Open(Save(builder.Build(events)));

        Assert.True(builder.ChunkFileOffsets.Count > 3, "test data should span several chunks");
        Assert.Equal(2500, file.Count);
        Assert.Equal(0, file.TotalIssueCount);

        foreach (var i in new[] { 0, 1, 499, 500, 1234, 2499 })
        {
            var detail = file.ReadDetail(file.FindByRecordId(events[i].RecordId));
            Assert.Equal(events[i].Provider, detail.Provider);
            Assert.Equal(events[i].EventId, detail.Summary.EventId);
            Assert.Equal(events[i].Param1, detail.Data[0].Value);
        }

        Assert.Equal(-1, file.FindByRecordId(99999));
        Assert.Equal(-1, file.FindByRecordId(0));
    }

    [Fact]
    public async Task Wrapped_logs_with_chunks_out_of_order_still_read_chronologically()
    {
        var events = Events(2500);
        var file = await Open(Save(new EvtxBuilder().Build(events, reverseChunkOrder: true)));

        Assert.Equal(2500, file.Count);
        Assert.Equal(events.Select(e => e.RecordId), Enumerable.Range(0, file.Count).Select(i => file.GetSummary(i).RecordId));
        Assert.Equal(0, file.TotalIssueCount);
    }

    [Fact]
    public async Task An_empty_log_opens_with_no_events()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Array.Empty<TestEvent>())));

        Assert.Equal(0, file.Count);
        Assert.Empty(file.Providers);
    }

    // ---- details ----

    [Fact]
    public async Task Detail_decodes_system_fields_data_and_xml()
    {
        var file = await Open(Save(new EvtxBuilder().Build(new[]
        {
            new TestEvent(42, "Schannel", 36871, EventLevels.Error, T0, "PC-02341", "alpha", "b<c&d\"e"),
        })));

        var detail = file.ReadDetail(0);

        Assert.Null(detail.DecodeError);
        Assert.Equal("Schannel", detail.Provider);
        Assert.Equal("PC-02341", detail.Computer);
        Assert.Equal(36871u, detail.Summary.EventId);
        Assert.Equal("EventData", detail.DataSection);
        Assert.Equal(new[] { ("Param1", "alpha"), ("Param2", "b<c&d\"e") }, detail.Data.Select(d => (d.Name!, d.Value)));

        // The XML is well formed and shows the escaped value and the time.
        var xml = System.Xml.Linq.XDocument.Parse(detail.Xml);
        Assert.Equal("Event", xml.Root!.Name.LocalName);
        Assert.Contains("b&lt;c&amp;d\"e", detail.Xml);
        Assert.Contains("SystemTime=\"2026-07-23T14:12:00.0000000Z\"", detail.Xml);
        Assert.Contains("<EventRecordID>42</EventRecordID>", detail.Xml);
        Assert.Contains("xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"", detail.Xml);
    }

    [Fact]
    public async Task Message_comes_from_the_formatter_and_receives_the_data_values()
    {
        var formatter = new RecordingFormatter();
        var file = await Open(Save(new EvtxBuilder().Build(Events(3))), formatter);

        var detail = file.ReadDetail(1);

        Assert.Equal("Schannel/7001: param one 1 | second <&> \"1\"", detail.Message);
        Assert.Null(formatter.LastQualifiers); // the file has no Qualifiers attribute, so the event is not classic
    }

    private sealed class RecordingFormatter : IEventMessageFormatter
    {
        public int? LastQualifiers { get; private set; } = 12345;

        public string? Format(string provider, uint eventId, int? qualifiers, int version, IReadOnlyList<EventDataItem> data)
        {
            LastQualifiers = qualifiers;
            return $"{provider}/{eventId}: {string.Join(" | ", data.Select(d => d.Value))}";
        }
    }

    [Fact]
    public async Task Search_text_contains_provider_and_data_values()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Events(3))));

        var text = file.ReadSearchText(1, includeMessage: false);

        Assert.Contains("Schannel", text);
        Assert.Contains("param one 1", text);
        Assert.Contains("Param2=", text);
    }

    [Fact]
    public async Task Search_text_includes_the_message_only_when_requested()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Events(3))), new RecordingFormatter());

        Assert.DoesNotContain("Schannel/7001:", file.ReadSearchText(1, includeMessage: false));
        Assert.Contains("Schannel/7001: param one 1", file.ReadSearchText(1, includeMessage: true));
    }

    [Fact]
    public async Task Requesting_the_message_without_a_formatter_is_harmless()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Events(3))));

        Assert.Equal(file.ReadSearchText(1, false), file.ReadSearchText(1, true));
    }

    [Fact]
    public async Task Concurrent_reads_are_safe_and_consistent()
    {
        var events = Events(2500);
        var file = await Open(Save(new EvtxBuilder().Build(events)));

        var failures = 0;
        Parallel.For(0, 2500, i =>
        {
            var detail = file.ReadDetail(i);
            if (detail.Data[0].Value != events[i].Param1)
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task No_file_handle_is_held_between_reads()
    {
        var path = Save(new EvtxBuilder().Build(Events(10)));
        var file = await Open(path);
        _ = file.ReadDetail(3);

        File.Delete(path);

        Assert.False(File.Exists(path));
    }

    // ---- damage ----

    [Fact]
    public async Task A_dirty_header_flag_is_reported()
    {
        var file = await Open(Save(new EvtxBuilder().Build(Events(5), dirty: true)));

        Assert.True(file.IsDirty);
        Assert.Equal(5, file.Count);
    }

    [Fact]
    public async Task A_bad_file_header_checksum_is_an_issue_but_not_fatal()
    {
        var bytes = new EvtxBuilder().Build(Events(5));
        bytes[0x7C] ^= 0xFF;

        var file = await Open(Save(bytes));

        Assert.Equal(5, file.Count);
        Assert.Contains(file.Issues, i => i.Message.Contains("File header checksum"));
    }

    [Fact]
    public async Task A_bad_record_checksum_is_reported_and_events_still_load()
    {
        var builder = new EvtxBuilder();
        var bytes = builder.Build(Events(30));
        // Alter a byte inside the first record's computer name text: structure intact, content changed.
        bytes[builder.RecordFileOffsets[0] + 24 + 9] ^= 0x01;

        var file = await Open(Save(bytes));

        Assert.Equal(30, file.Count);
        Assert.Contains(file.Issues, i => i.Message.Contains("record checksum"));
    }

    [Fact]
    public async Task A_corrupt_record_ends_that_chunk_and_the_other_chunks_are_unaffected()
    {
        var builder = new EvtxBuilder();
        var events = Events(2500);
        var bytes = builder.Build(events);
        var chunkCount = builder.ChunkFileOffsets.Count;
        var victim = 700; // somewhere in a middle chunk
        bytes[builder.RecordFileOffsets[victim]] = 0x00; // destroy the record signature

        var file = await Open(Save(bytes));

        Assert.Contains(file.Issues, i => i.Message.Contains("invalid record"));
        Assert.True(file.Count < 2500 && file.Count > 2500 - 700);
        Assert.NotEqual(-1, file.FindByRecordId(events[0].RecordId));
        Assert.NotEqual(-1, file.FindByRecordId(events[2499].RecordId));
        Assert.True(chunkCount > 3);
    }

    [Fact]
    public async Task A_garbage_chunk_is_skipped_with_an_issue_and_a_zeroed_chunk_is_skipped_silently()
    {
        var builder = new EvtxBuilder();
        var events = Events(2500);
        var bytes = builder.Build(events);
        var garbage = (int)builder.ChunkFileOffsets[1];
        for (var i = 0; i < 600; i++)
        {
            bytes[garbage + i] = (byte)(i * 7 + 3);
        }

        var zeroed = (int)builder.ChunkFileOffsets[2];
        Array.Clear(bytes, zeroed, 65536);

        var file = await Open(Save(bytes));

        Assert.True(file.Count < 2500);
        Assert.Single(file.Issues, i => i.Message.Contains("invalid signature"));
        Assert.DoesNotContain(file.Issues, i => i.FileOffset == zeroed);
    }

    [Fact]
    public async Task A_truncated_file_keeps_the_events_before_the_cut()
    {
        var builder = new EvtxBuilder();
        var bytes = builder.Build(Events(2500));
        var cut = (int)builder.ChunkFileOffsets[2] + 20_000;

        var file = await Open(Save(bytes[..cut]));

        Assert.True(file.Count > 0 && file.Count < 2500);
        Assert.Contains(file.Issues, i => i.Message.Contains("truncated") || i.Message.Contains("invalid"));
        Assert.NotNull(file.ReadDetail(0).Provider);
    }

    [Fact]
    public async Task A_record_that_cannot_be_decoded_is_listed_and_reports_its_error()
    {
        var builder = new EvtxBuilder();
        var events = Events(5);
        var bytes = builder.Build(events);
        // Replace the template-instance token (first token after the 4-byte fragment header) with an invalid one.
        bytes[builder.RecordFileOffsets[2] + 24 + 4] = 0xFF;

        var file = await Open(Save(bytes));

        Assert.Equal(5, file.Count);
        Assert.Contains(file.Issues, i => i.Message.Contains($"Record {events[2].RecordId} could not be decoded"));
        var summary = file.GetSummary(2);
        Assert.Equal("(unreadable record)", file.ProviderName(summary.ProviderId));
        Assert.Equal(events[2].RecordId, summary.RecordId);

        var detail = file.ReadDetail(2);
        Assert.NotNull(detail.DecodeError);
        Assert.StartsWith("<!--", detail.Xml);

        // Neighbours are fine.
        Assert.Null(file.ReadDetail(1).DecodeError);
        Assert.Null(file.ReadDetail(3).DecodeError);
    }

    [Fact]
    public async Task A_template_that_instantiates_itself_fails_safely_instead_of_recursing_forever()
    {
        var builder = new EvtxBuilder();
        var bytes = builder.Build(Events(3));
        var record = (int)builder.RecordFileOffsets[0];

        // Layout of the first record: header 24, fragment 4, template token 6, definition offset field 4, then the
        // inline definition (next 4, guid 16, size 4) and its body. Point the body at an instance of itself.
        var instance = record + 24 + 4;
        var definitionStart = instance + 10;
        var bodyStart = definitionStart + 24;
        var chunkStart = (int)builder.ChunkFileOffsets[0];
        var selfOffset = (uint)(definitionStart - chunkStart);
        bytes[bodyStart] = 0x0C;                     // template instance
        bytes[bodyStart + 1] = 1;
        BitConverter.GetBytes(1u).CopyTo(bytes, bodyStart + 2);
        BitConverter.GetBytes(selfOffset).CopyTo(bytes, bodyStart + 6);
        BitConverter.GetBytes(0u).CopyTo(bytes, bodyStart + 10); // zero substitutions

        var file = await Open(Save(bytes));

        var detail = file.ReadDetail(0);
        Assert.NotNull(detail.DecodeError);
        Assert.Contains("too deep", detail.DecodeError);
    }

    // ---- not an event log ----

    [Fact]
    public async Task Non_evtx_files_are_rejected_clearly()
    {
        var random = Save(Enumerable.Range(0, 10_000).Select(i => (byte)(i * 31)).ToArray());
        var tiny = Save(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<EvtxFormatException>(() => Open(random));
        await Assert.ThrowsAsync<EvtxFormatException>(() => Open(tiny));
    }

    [Fact]
    public async Task Opening_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EvtxFile.OpenAsync(Save(new EvtxBuilder().Build(Events(2500))), null, cts.Token));
    }

    // ---- parser ----

    [Fact]
    public async Task The_parser_produces_an_event_log_document_and_turns_bad_files_into_failures()
    {
        var good = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = "x.evtx",
            OriginalPath = "x.evtx",
            ExtractedPath = Save(new EvtxBuilder().Build(Events(20), dirty: true)),
            ArtifactType = ArtifactType.EventLog,
        };
        var parser = new EvtxParser();

        Assert.True(parser.CanHandle(good));
        var document = Assert.IsType<EventLogDocument>(await parser.ParseAsync(good, CancellationToken.None));
        Assert.Equal(20, document.Source.Count);
        Assert.True(document.IsDirty);

        var bad = good with { ExtractedPath = Save(new byte[] { 1, 2, 3 }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(bad, CancellationToken.None));

        Assert.False(parser.CanHandle(good with { ArtifactType = ArtifactType.TextLog }));
    }
}
