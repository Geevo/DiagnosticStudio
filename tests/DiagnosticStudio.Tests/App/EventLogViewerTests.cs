using DiagnosticStudio.App.ViewModels.EventLogViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Parsers.Evtx;
using DiagnosticStudio.Tests.Evtx;

namespace DiagnosticStudio.Tests.App;

public sealed class EventLogViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-evvm-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);

    public EventLogViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly TestEvent[] Sample =
    {
        new(100, "Service Control Manager", 7036, EventLevels.Information, T0, "PC", "Print Spooler", "running"),
        new(101, "Service Control Manager", 7031, EventLevels.Error, T0.AddSeconds(1), "PC", "Print Spooler", "terminated unexpectedly"),
        new(102, "Application Error", 1000, EventLevels.Error, T0.AddSeconds(2), "PC", "Agent.exe", "0xc0000005"),
        new(103, "Schannel", 36871, EventLevels.Error, T0.AddSeconds(3), "PC", "TLS client credential", "failed"),
        new(104, "Schannel", 36874, EventLevels.Warning, T0.AddSeconds(4), "PC", "TLS 1.2", "received"),
        new(105, "Microsoft-Windows-Kernel-General", 12, EventLevels.Information, T0.AddSeconds(5), "PC", "started", ""),
        new(106, "Service Control Manager", 7036, EventLevels.Verbose, T0.AddSeconds(6), "PC", "Windows Update", "stopped"),
    };

    private (EventLogViewerViewModel Vm, EvtxFile File) Open(IReadOnlyList<TestEvent>? events = null, IEventMessageFormatter? formatter = null, bool dirty = false)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".evtx");
        File.WriteAllBytes(path, new EvtxBuilder().Build(events ?? Sample, dirty));
        var file = EvtxFile.OpenAsync(path, formatter, CancellationToken.None).GetAwaiter().GetResult();
        var doc = new EventLogDocument
        {
            Artifact = new DiagnosticArtifact
            {
                Id = Guid.NewGuid(),
                Name = "t.evtx",
                OriginalPath = "t.evtx",
                ExtractedPath = path,
                ArtifactType = ArtifactType.EventLog,
            },
            Source = file,
            Issues = file.Issues,
            TotalIssueCount = file.TotalIssueCount,
            IsDirty = file.IsDirty,
        };
        return (new EventLogViewerViewModel(doc), file);
    }

    private static async Task Settle(EventLogViewerViewModel vm) => await vm.PendingFilter;

    private static long[] Shown(EventLogViewerViewModel vm) => vm.Events.Select(r => r.RecordId).ToArray();

    // ---- opening ----

    [Fact]
    public void Opens_showing_every_event_in_order_with_status_and_no_banner()
    {
        var (vm, _) = Open();

        Assert.Equal(new long[] { 100, 101, 102, 103, 104, 105, 106 }, Shown(vm));
        Assert.Equal("7 events", vm.StatusText);
        Assert.Null(vm.WarningText);
        Assert.False(vm.HasActiveFilter);
        Assert.Null(vm.SelectedEvent);
        Assert.Equal("All providers", vm.ProviderOptions[0].Label);
        Assert.Contains(vm.ProviderOptions, p => p.Label == "Service Control Manager (3)");
    }

    [Fact]
    public void A_dirty_log_shows_a_banner()
    {
        var (vm, _) = Open(dirty: true);

        Assert.Contains("not cleanly closed", vm.WarningText);
    }

    [Fact]
    public void Rows_expose_index_facts_and_UTC_time()
    {
        var (vm, _) = Open();
        var row = vm.Events[1];

        Assert.Equal("Service Control Manager", row.Provider);
        Assert.Equal(7031u, row.EventId);
        Assert.Equal("Error", row.LevelName);
        Assert.Equal("Error", row.Severity);
        Assert.Equal("2026-07-23 14:12:01.000", row.TimeText);
        Assert.Equal(string.Empty, vm.Events[0].Severity);
    }

    // ---- filtering ----

    [Fact]
    public async Task Level_filter_keeps_that_level_or_worse()
    {
        var (vm, _) = Open();

        vm.SelectedLevel = EventLogViewerViewModel.LevelOptions.Single(o => o.Label == "Error or worse");
        await Settle(vm);

        Assert.Equal(new long[] { 101, 102, 103 }, Shown(vm));
        Assert.Equal("3 of 7 events", vm.StatusText);
        Assert.True(vm.HasActiveFilter);

        vm.SelectedLevel = EventLogViewerViewModel.LevelOptions.Single(o => o.Label == "Information or worse");
        await Settle(vm);
        Assert.Equal(new long[] { 100, 101, 102, 103, 104, 105 }, Shown(vm)); // verbose excluded
    }

    [Fact]
    public async Task Provider_filter_keeps_one_provider()
    {
        var (vm, _) = Open();

        vm.SelectedProvider = vm.ProviderOptions.Single(p => p.Label.StartsWith("Schannel"));
        await Settle(vm);

        Assert.Equal(new long[] { 103, 104 }, Shown(vm));
    }

    [Fact]
    public async Task Event_id_filter_accepts_lists_and_ranges_and_reports_bad_input()
    {
        var (vm, _) = Open();

        vm.EventIdText = "7031, 36871-36874";
        await Settle(vm);
        Assert.Equal(new long[] { 101, 103, 104 }, Shown(vm));
        Assert.Null(vm.EventIdError);

        vm.EventIdText = "not a number";
        await Settle(vm);
        Assert.NotNull(vm.EventIdError);
        Assert.Equal(7, vm.Events.Count); // an invalid id filter does not hide events
    }

    [Fact]
    public async Task Text_filter_searches_provider_event_id_and_data_values()
    {
        var (vm, _) = Open();

        vm.FindText = "print spooler";
        await Settle(vm);
        Assert.Equal(new long[] { 100, 101 }, Shown(vm));

        vm.FindText = "0xC0000005";
        await Settle(vm);
        Assert.Equal(new long[] { 102 }, Shown(vm));

        vm.FindText = "7036";
        await Settle(vm);
        Assert.Equal(new long[] { 100, 106 }, Shown(vm));

        vm.MatchCase = true;
        vm.FindText = "PRINT";
        await Settle(vm);
        Assert.Empty(Shown(vm));
        Assert.Equal("0 of 7 events", vm.StatusText);
    }

    [Fact]
    public async Task Filters_combine_and_clear_restores_everything()
    {
        var (vm, _) = Open();
        vm.SelectedLevel = EventLogViewerViewModel.LevelOptions.Single(o => o.Label == "Error or worse");
        vm.SelectedProvider = vm.ProviderOptions.Single(p => p.Label.StartsWith("Service Control"));
        vm.FindText = "spooler";
        await Settle(vm);
        Assert.Equal(new long[] { 101 }, Shown(vm));

        vm.ClearFiltersCommand.Execute(null);

        Assert.Equal(7, vm.Events.Count);
        Assert.False(vm.HasActiveFilter);
        Assert.Equal(string.Empty, vm.FindText);
        Assert.Equal("All providers", vm.SelectedProvider.Label);
        Assert.Equal("7 events", vm.StatusText);
    }

    [Fact]
    public async Task Newest_first_reverses_the_rows()
    {
        var (vm, _) = Open();

        vm.NewestFirst = true;
        await Settle(vm);

        Assert.Equal(new long[] { 106, 105, 104, 103, 102, 101, 100 }, Shown(vm));
        Assert.Equal(6, vm.Events.RowOfEvent(0));
        Assert.Equal(0, vm.Events.RowOfEvent(6));
    }

    // ---- selection ----

    [Fact]
    public async Task Selecting_a_row_loads_its_detail()
    {
        var (vm, _) = Open();

        vm.SelectedEvent = vm.Events[1];

        var detail = vm.SelectedDetail!;
        Assert.Equal("Event 7031 · Service Control Manager", detail.Title);
        Assert.Contains(detail.Fields, f => f is { Label: "Provider", Value: "Service Control Manager" });
        Assert.Contains(detail.Fields, f => f is { Label: "Record ID", Value: "101" });
        Assert.Contains(detail.Fields, f => f is { Label: "Computer", Value: "PC" });
        Assert.Equal(new[] { ("Param1", "Print Spooler"), ("Param2", "terminated unexpectedly") }, detail.Data.Select(d => (d.Label, d.Value)));
        Assert.Equal("EVENTDATA", detail.DataTitle);
        Assert.Contains("<EventRecordID>101</EventRecordID>", detail.Xml);
        Assert.Equal(EventDetailViewModel.NoMessageNote, detail.Note);
        Assert.False(detail.HasMessage);

        vm.SelectedEvent = null;
        Assert.Null(vm.SelectedDetail);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_selection_survives_a_filter_that_still_includes_it_and_clears_otherwise()
    {
        var (vm, _) = Open();
        var scrolled = new List<int>();
        vm.ScrollToRowRequested += (_, row) => scrolled.Add(row);
        vm.SelectedEvent = vm.Events[3]; // record 103 (Schannel, error)

        vm.SelectedLevel = EventLogViewerViewModel.LevelOptions.Single(o => o.Label == "Error or worse");
        await Settle(vm);

        Assert.Equal(103, vm.SelectedEvent!.RecordId);
        Assert.Equal(new[] { 2 }, scrolled); // third row of the filtered view

        vm.SelectedProvider = vm.ProviderOptions.Single(p => p.Label.StartsWith("Application Error"));
        await Settle(vm);

        Assert.Null(vm.SelectedEvent);
        Assert.Null(vm.SelectedDetail);
    }

    // ---- messages ----

    private sealed class EchoFormatter : IEventMessageFormatter
    {
        public string? Format(string provider, uint eventId, int? qualifiers, int version, IReadOnlyList<EventDataItem> data) =>
            provider == "Schannel" ? $"First line of {eventId}\r\nsecond line {data[0].Value}" : null;
    }

    [Fact]
    public void Messages_appear_in_the_table_preview_and_the_detail_when_a_formatter_provides_them()
    {
        var (vm, _) = Open(formatter: new EchoFormatter());

        var withMessage = vm.Events[3];
        Assert.True(withMessage.HasMessage);
        Assert.Equal("First line of 36871", withMessage.MessagePreview);

        var without = vm.Events[0];
        Assert.False(without.HasMessage);
        Assert.Equal("Param1=Print Spooler, Param2=running", without.MessagePreview);

        vm.SelectedEvent = withMessage;
        Assert.True(vm.SelectedDetail!.HasMessage);
        Assert.Contains("second line TLS client credential", vm.SelectedDetail.Message);
        Assert.Equal(EventDetailViewModel.MessageProvenanceNote, vm.SelectedDetail.Note);
    }

    [Fact]
    public async Task Message_text_is_searchable_only_when_Include_messages_is_ticked()
    {
        var (vm, _) = Open(formatter: new EchoFormatter());

        vm.FindText = "first line of 36874"; // appears only in the rendered message of record 104
        await Settle(vm);
        Assert.Empty(Shown(vm));
        Assert.Equal("0 of 7 events", vm.StatusText);

        vm.IncludeMessage = true;
        await Settle(vm);
        Assert.Equal(new long[] { 104 }, Shown(vm));

        vm.IncludeMessage = false;
        await Settle(vm);
        Assert.Empty(Shown(vm));
    }

    [Fact]
    public async Task Toggling_Include_messages_without_a_query_does_not_filter()
    {
        var (vm, _) = Open(formatter: new EchoFormatter());

        vm.IncludeMessage = true;
        await Settle(vm);

        Assert.Equal(7, vm.Events.Count);
        Assert.False(vm.HasActiveFilter);
    }

    // ---- navigation ----

    [Fact]
    public void Navigating_to_an_event_record_selects_and_scrolls_to_it()
    {
        var (vm, _) = Open();
        var scrolled = new List<int>();
        vm.ScrollToRowRequested += (_, row) => scrolled.Add(row);
        vm.SelectedTabIndex = EventLogViewerViewModel.XmlTab;

        var ok = vm.NavigateTo(DiagnosticLocation.ForEventRecord(vm.Document.Artifact.Id, 104));

        Assert.True(ok);
        Assert.Equal(104, vm.SelectedEvent!.RecordId);
        Assert.Equal(new[] { 4 }, scrolled);
        Assert.Equal(EventLogViewerViewModel.DetailsTab, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task Navigating_to_an_event_hidden_by_a_filter_clears_the_filters_so_the_evidence_is_visible()
    {
        var (vm, _) = Open();
        vm.FindText = "schannel";
        await Settle(vm);
        Assert.Equal(2, vm.Events.Count);

        var ok = vm.NavigateTo(DiagnosticLocation.ForEventRecord(vm.Document.Artifact.Id, 100));

        Assert.True(ok);
        Assert.Equal(7, vm.Events.Count);
        Assert.Equal(string.Empty, vm.FindText);
        Assert.Equal(100, vm.SelectedEvent!.RecordId);
    }

    [Fact]
    public void Unknown_records_and_other_location_kinds_are_not_resolved()
    {
        var (vm, _) = Open();
        var id = vm.Document.Artifact.Id;

        Assert.False(vm.NavigateTo(DiagnosticLocation.ForEventRecord(id, 99999)));
        Assert.False(vm.NavigateTo(DiagnosticLocation.ForLine(id, 3)));
        Assert.False(vm.NavigateTo(DiagnosticLocation.ForArtifact(id)));
    }

    // ---- virtual list ----

    [Fact]
    public void The_virtual_list_serves_pages_stays_correct_after_eviction_and_maps_items_to_rows()
    {
        var events = Enumerable.Range(0, 6000)
            .Select(i => new TestEvent(1000 + i, "P" + (i % 5), (uint)i, EventLevels.Information, T0.AddSeconds(i), "PC", "a", "b"))
            .ToList();
        var (vm, _) = Open(events);
        var list = vm.Events;

        Assert.Equal(6000, list.Count);
        for (var page = 0; page < 6000; page += VirtualEventList.PageSize)
        {
            Assert.Equal(1000 + page, list[page].RecordId);
        }

        Assert.Equal(1000, list[0].RecordId); // earliest page was evicted and rebuilt
        var row = list[4321];
        Assert.Equal(4321, ((System.Collections.IList)list).IndexOf(row));
        Assert.Equal(-1, ((System.Collections.IList)list).IndexOf("nope"));
        Assert.Throws<ArgumentOutOfRangeException>(() => list[6000]);
    }

    [Fact]
    public async Task An_undecodable_record_still_shows_a_row_with_a_clear_preview()
    {
        var builder = new EvtxBuilder();
        var bytes = builder.Build(Sample);
        bytes[builder.RecordFileOffsets[2] + 24 + 4] = 0xFF;
        var path = Path.Combine(_dir, "bad.evtx");
        File.WriteAllBytes(path, bytes);
        var file = await EvtxFile.OpenAsync(path, null, CancellationToken.None);
        var doc = new EventLogDocument
        {
            Artifact = new DiagnosticArtifact { Id = Guid.NewGuid(), Name = "bad.evtx", OriginalPath = "bad.evtx", ArtifactType = ArtifactType.EventLog },
            Source = file,
            Issues = file.Issues,
            TotalIssueCount = file.TotalIssueCount,
        };

        var vm = new EventLogViewerViewModel(doc);

        Assert.Equal(7, vm.Events.Count);
        Assert.Equal("(record could not be decoded)", vm.Events[2].MessagePreview);
        Assert.Contains("problems were found", vm.WarningText);
        vm.SelectedEvent = vm.Events[2];
        Assert.StartsWith("This record could not be fully decoded", vm.SelectedDetail!.Note);
    }
}
