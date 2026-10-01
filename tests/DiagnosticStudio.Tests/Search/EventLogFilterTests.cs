using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.Tests.Search;

public class EventLogFilterTests
{
    internal sealed class ListEventSource : IEventLogSource
    {
        private readonly List<(long Id, byte Level, ushort Provider, uint EventId, string Text)> _events = new();
        private readonly List<string> _providers = new();

        public int Count => _events.Count;

        public IReadOnlyList<EventLogProvider> Providers =>
            _providers.Select((p, i) => new EventLogProvider((ushort)i, p, _events.Count(e => e.Provider == i))).ToList();

        public ListEventSource Add(string provider, uint eventId, byte level, string text = "")
        {
            var index = _providers.IndexOf(provider);
            if (index < 0)
            {
                _providers.Add(provider);
                index = _providers.Count - 1;
            }

            _events.Add((_events.Count + 100, level, (ushort)index, eventId, text));
            return this;
        }

        public string ProviderName(ushort providerId) => _providers[providerId];

        public EventSummary GetSummary(int index)
        {
            var e = _events[index];
            return new EventSummary(index, e.Id, DateTime.UnixEpoch.AddMinutes(index), e.Level, e.Provider, e.EventId);
        }

        public int FindByRecordId(long recordId) => _events.FindIndex(e => e.Id == recordId);

        public EventDetail ReadDetail(int index) => throw new NotSupportedException();

        public string ReadSearchText(int index, bool includeMessage) =>
            _providers[_events[index].Provider] + "\n" + _events[index].Text
            + (includeMessage ? "\nMESSAGE for event " + _events[index].EventId : string.Empty);
    }

    private static ListEventSource Sample() => new ListEventSource()
        .Add("Service Control Manager", 7036, EventLevels.Information, "Print Spooler service entered the running state")
        .Add("Service Control Manager", 7031, EventLevels.Error, "Print Spooler terminated unexpectedly")
        .Add("Application Error", 1000, EventLevels.Error, "Faulting application Agent.exe")
        .Add("Schannel", 36871, EventLevels.Error, "TLS client credential failed")
        .Add("Schannel", 36874, EventLevels.Warning, "TLS 1.2 connection request was received")
        .Add("Microsoft-Windows-Kernel-General", 12, EventLevels.Information, "operating system started");

    // ---- EventIdSet ----

    [Theory]
    [InlineData("7031", new[] { 7031u }, new[] { 7036u, 0u })]
    [InlineData("7031, 7036", new[] { 7031u, 7036u }, new[] { 7000u })]
    [InlineData("100-200", new[] { 100u, 150u, 200u }, new[] { 99u, 201u })]
    [InlineData("1;2 3", new[] { 1u, 2u, 3u }, new[] { 4u })]
    [InlineData("0x10", new[] { 16u }, new[] { 10u })]
    [InlineData("5, 10-12, 0x20-0x22", new[] { 5u, 11u, 33u }, new[] { 6u, 13u, 35u })]
    public void Id_sets_accept_lists_ranges_and_hex(string text, uint[] included, uint[] excluded)
    {
        Assert.True(EventIdSet.TryParse(text, out var set, out var error), error);

        Assert.All(included, id => Assert.True(set!.Contains(id), id.ToString()));
        Assert.All(excluded, id => Assert.False(set!.Contains(id), id.ToString()));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12-x")]
    [InlineData("20-10")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void Invalid_id_text_is_rejected_with_a_message(string text)
    {
        Assert.False(EventIdSet.TryParse(text, out var set, out var error));
        Assert.Null(set);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_id_text_means_no_filter(string? text)
    {
        Assert.True(EventIdSet.TryParse(text, out var set, out var error));
        Assert.Null(set);
        Assert.Null(error);
    }

    // ---- filter ----

    [Fact]
    public void Empty_criteria_return_null_meaning_everything()
    {
        Assert.Null(EventLogFilter.Apply(Sample(), new EventFilterCriteria(), CancellationToken.None));
    }

    [Fact]
    public void Level_filter_keeps_only_the_chosen_levels()
    {
        var result = EventLogFilter.Apply(
            Sample(), new EventFilterCriteria { Levels = new HashSet<byte> { EventLevels.Error } }, CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void Provider_filter_keeps_only_the_chosen_providers()
    {
        var source = Sample();
        var schannel = source.Providers.Single(p => p.Name == "Schannel").Id;

        var result = EventLogFilter.Apply(
            source, new EventFilterCriteria { ProviderIds = new HashSet<ushort> { schannel } }, CancellationToken.None);

        Assert.Equal(new[] { 3, 4 }, result);
    }

    [Fact]
    public void Event_id_filter_applies_ranges()
    {
        EventIdSet.TryParse("7031, 36871-36874", out var ids, out _);

        var result = EventLogFilter.Apply(Sample(), new EventFilterCriteria { EventIds = ids }, CancellationToken.None);

        Assert.Equal(new[] { 1, 3, 4 }, result);
    }

    [Fact]
    public void Text_filter_matches_provider_data_and_event_id_case_insensitively_by_default()
    {
        var source = Sample();

        Assert.Equal(new[] { 0, 1 }, EventLogFilter.Apply(source, new EventFilterCriteria { Text = "print spooler" }, CancellationToken.None));
        Assert.Equal(new[] { 3, 4 }, EventLogFilter.Apply(source, new EventFilterCriteria { Text = "SCHANNEL" }, CancellationToken.None));
        Assert.Equal(new[] { 1 }, EventLogFilter.Apply(source, new EventFilterCriteria { Text = "7031" }, CancellationToken.None));
    }

    [Fact]
    public void Message_text_is_only_searched_when_asked_for()
    {
        var source = Sample();

        Assert.Empty(EventLogFilter.Apply(source, new EventFilterCriteria { Text = "message for event 7031" }, CancellationToken.None)!);
        Assert.Equal(
            new[] { 1 },
            EventLogFilter.Apply(source, new EventFilterCriteria { Text = "message for event 7031", IncludeMessage = true }, CancellationToken.None));
    }

    [Fact]
    public void IncludeMessage_alone_is_not_a_filter()
    {
        Assert.True(new EventFilterCriteria { IncludeMessage = true }.IsEmpty);
    }

    [Fact]
    public void Text_filter_can_be_case_sensitive()
    {
        var source = Sample();

        Assert.Empty(EventLogFilter.Apply(source, new EventFilterCriteria { Text = "print spooler", MatchCase = true }, CancellationToken.None)!);
        Assert.Equal(new[] { 0, 1 }, EventLogFilter.Apply(source, new EventFilterCriteria { Text = "Print Spooler", MatchCase = true }, CancellationToken.None));
    }

    [Fact]
    public void All_criteria_must_match_together()
    {
        var source = Sample();
        var scm = source.Providers.Single(p => p.Name == "Service Control Manager").Id;
        var criteria = new EventFilterCriteria
        {
            ProviderIds = new HashSet<ushort> { scm },
            Levels = new HashSet<byte> { EventLevels.Error },
            Text = "spooler",
        };

        Assert.Equal(new[] { 1 }, EventLogFilter.Apply(source, criteria, CancellationToken.None));
    }

    [Fact]
    public void Filter_with_no_matches_returns_an_empty_array_not_null()
    {
        var result = EventLogFilter.Apply(Sample(), new EventFilterCriteria { Text = "no such text" }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => EventLogFilter.ApplyAsync(Sample(), new EventFilterCriteria { Text = "x" }, cts.Token));
    }
}
