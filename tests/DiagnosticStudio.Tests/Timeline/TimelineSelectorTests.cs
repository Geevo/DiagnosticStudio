using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Timeline;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Timeline;

public class TimelineSelectorTests
{
    private static readonly DateTime Noon = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    private static long T(int minutes) => Noon.AddMinutes(minutes).Ticks;

    private static TimelineEntry E(int minutes, int source, long position, LogSeverity severity = LogSeverity.Information, bool unzoned = false) =>
        new(T(minutes), position, source, severity, unzoned);

    /// <summary>Source 0 is an event log, source 1 and 2 are text logs.</summary>
    private static TimelineIndex Index(params TimelineEntry[] entries)
    {
        var artifacts = new[]
        {
            Artifact("system.evtx", ArtifactType.EventLog),
            Artifact("agent.log", ArtifactType.TextLog),
            Artifact("setup.log", ArtifactType.TextLog),
        };
        var sources = artifacts
            .Select((a, i) => new TimelineSource(i, a, i == 0 ? TimelineSourceKind.EventLog : TimelineSourceKind.TextLog, entries.Count(e => e.Source == i), entries.Count(e => e.Source == i && e.Unzoned), 0))
            .ToList();
        return new TimelineIndex(sources, entries, 3, 0, Array.Empty<string>());
    }

    private static string Where(TimelineIndex index, int[] order) =>
        string.Join(" ", order.Select(i => index.Entries[i].Source + ":" + index.Entries[i].Position));

    [Fact]
    public void Entries_of_all_sources_are_merged_in_time_order()
    {
        var index = Index(
            E(0, 0, 100), E(10, 0, 101), E(20, 0, 102),
            E(5, 1, 1), E(15, 1, 2),
            E(1, 2, 7));

        Assert.Equal("0:100 2:7 1:1 0:101 1:2 0:102", Where(index, TimelineSelector.Select(index, new TimelineFilter())));
    }

    [Fact]
    public void Entries_with_the_same_time_keep_source_then_position_order()
    {
        var index = Index(E(5, 0, 9), E(5, 0, 10), E(5, 1, 1), E(5, 1, 2), E(5, 2, 1));

        Assert.Equal("0:9 0:10 1:1 1:2 2:1", Where(index, TimelineSelector.Select(index, new TimelineFilter())));
    }

    [Fact]
    public void Ordering_is_the_same_every_time_however_large()
    {
        var entries = new List<TimelineEntry>();
        var random = new Random(7);
        for (var s = 0; s < 3; s++)
        {
            for (var n = 1; n <= 2000; n++)
            {
                // Few distinct minutes, so there are many ties.
                entries.Add(E(random.Next(0, 40), s, n));
            }
        }

        var index = Index(entries.ToArray());
        var first = TimelineSelector.Select(index, new TimelineFilter());
        var second = TimelineSelector.Select(index, new TimelineFilter());

        Assert.Equal(first, second);
        for (var i = 1; i < first.Length; i++)
        {
            var a = index.Entries[first[i - 1]];
            var b = index.Entries[first[i]];
            Assert.True(a.Ticks < b.Ticks || (a.Ticks == b.Ticks && first[i - 1] < first[i]));
        }
    }

    [Fact]
    public void An_unzoned_time_is_moved_by_its_sources_offset()
    {
        // Written 14:00 in a log that is UTC+2 is 12:00 UTC, so it comes before an event at 12:30 UTC.
        var index = Index(E(30, 0, 1), E(120, 1, 1, unzoned: true));
        var offsets = new[] { TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.Zero };

        Assert.Equal("1:1 0:1", Where(index, TimelineSelector.Select(index, new TimelineFilter { SourceOffsets = offsets })));
        Assert.Equal("0:1 1:1", Where(index, TimelineSelector.Select(index, new TimelineFilter())));
    }

    [Fact]
    public void An_offset_never_moves_a_time_that_has_a_zone()
    {
        var index = Index(E(30, 1, 1, unzoned: false), E(40, 1, 2, unzoned: true));
        var offsets = new[] { TimeSpan.Zero, TimeSpan.FromHours(3), TimeSpan.Zero };

        Assert.Equal(T(30), TimelineSelector.EffectiveTicks(index.Entries[0], offsets));
        Assert.Equal(T(40) - TimeSpan.FromHours(3).Ticks, TimelineSelector.EffectiveTicks(index.Entries[1], offsets));
    }

    [Fact]
    public void An_offset_cannot_push_a_time_outside_the_representable_range()
    {
        var entry = new TimelineEntry(DateTime.MinValue.Ticks + 1, 1, 0, LogSeverity.None, Unzoned: true);

        var ticks = TimelineSelector.EffectiveTicks(entry, new[] { TimeSpan.FromHours(14) });

        Assert.Equal(DateTime.MinValue.Ticks, ticks);
    }

    [Fact]
    public void Excluded_sources_are_left_out()
    {
        var index = Index(E(0, 0, 1), E(1, 1, 1), E(2, 2, 1));

        var order = TimelineSelector.Select(index, new TimelineFilter { IncludedSources = new[] { true, false, true } });

        Assert.Equal("0:1 2:1", Where(index, order));
    }

    [Theory]
    [InlineData(TimelineSeverityFilter.All, "None Debug Information Warning Error")]
    [InlineData(TimelineSeverityFilter.HideDebug, "None Information Warning Error")]
    [InlineData(TimelineSeverityFilter.WarningsAndErrors, "Warning Error")]
    [InlineData(TimelineSeverityFilter.ErrorsOnly, "Error")]
    public void Severity_filters_keep_what_they_say(TimelineSeverityFilter filter, string expected)
    {
        var index = Index(
            E(0, 1, 1, LogSeverity.None), E(1, 1, 2, LogSeverity.Debug), E(2, 1, 3, LogSeverity.Information),
            E(3, 1, 4, LogSeverity.Warning), E(4, 1, 5, LogSeverity.Error));

        var order = TimelineSelector.Select(index, new TimelineFilter { Severity = filter });

        Assert.Equal(expected, string.Join(" ", order.Select(i => index.Entries[i].Severity)));
    }

    [Fact]
    public void The_time_range_is_inclusive_at_both_ends_and_applies_after_offsets()
    {
        var index = Index(E(0, 0, 1), E(10, 0, 2), E(20, 0, 3), E(150, 1, 1, unzoned: true));
        var offsets = new[] { TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.Zero };

        var order = TimelineSelector.Select(
            index,
            new TimelineFilter { FromUtc = Noon.AddMinutes(10), ToUtc = Noon.AddMinutes(30), SourceOffsets = offsets });

        // 12:10 and 12:20 qualify; 14:30 written in UTC+2 is 12:30 UTC and qualifies at the inclusive end.
        Assert.Equal("0:2 0:3 1:1", Where(index, order));
    }

    [Fact]
    public void An_empty_result_is_an_empty_array()
    {
        var index = Index(E(0, 0, 1));

        Assert.Empty(TimelineSelector.Select(index, new TimelineFilter { FromUtc = Noon.AddHours(1) }));
        Assert.Empty(TimelineSelector.Select(TimelineIndex.Empty, new TimelineFilter()));
    }

    [Fact]
    public void Selecting_can_be_cancelled()
    {
        var entries = Enumerable.Range(0, 200_000).Select(i => E(i % 100, 1, i + 1)).ToArray();
        var index = Index(entries);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => TimelineSelector.Select(index, new TimelineFilter(), cts.Token));
    }

    [Fact]
    public void RowAtOrAfter_finds_the_first_entry_not_before_a_time()
    {
        var index = Index(E(0, 0, 1), E(10, 0, 2), E(10, 1, 1), E(30, 0, 3));
        var order = TimelineSelector.Select(index, new TimelineFilter());

        Assert.Equal(0, TimelineSelector.RowAtOrAfter(index, order, null, T(-5)));
        Assert.Equal(1, TimelineSelector.RowAtOrAfter(index, order, null, T(10)));
        Assert.Equal(3, TimelineSelector.RowAtOrAfter(index, order, null, T(11)));
        Assert.Equal(3, TimelineSelector.RowAtOrAfter(index, order, null, T(999)));
        Assert.Equal(-1, TimelineSelector.RowAtOrAfter(index, Array.Empty<int>(), null, T(0)));
    }

    // ---- locations ----

    [Fact]
    public void Entries_link_back_to_their_source_by_line_or_record()
    {
        var index = Index(E(0, 0, 4242), E(1, 1, 17));

        var record = index.LocationOf(index.Entries[0]);
        var line = index.LocationOf(index.Entries[1]);

        Assert.Equal(DiagnosticLocation.ForEventRecord(index.Sources[0].Artifact.Id, 4242), record);
        Assert.Equal(DiagnosticLocation.ForLine(index.Sources[1].Artifact.Id, 17), line);
    }

    [Fact]
    public void FindEntry_matches_an_event_by_record_id_and_a_line_exactly()
    {
        var index = Index(E(0, 0, 100), E(1, 0, 101), E(2, 1, 5), E(3, 1, 9));

        Assert.Equal(1, index.FindEntry(DiagnosticLocation.ForEventRecord(index.Sources[0].Artifact.Id, 101)));
        Assert.Equal(3, index.FindEntry(DiagnosticLocation.ForLine(index.Sources[1].Artifact.Id, 9)));
        Assert.Equal(-1, index.FindEntry(DiagnosticLocation.ForEventRecord(index.Sources[0].Artifact.Id, 999)));
    }

    [Fact]
    public void A_line_without_a_time_belongs_to_the_timestamped_line_above_it()
    {
        var index = Index(E(2, 1, 5), E(3, 1, 9), E(4, 2, 7));
        var artifact = index.Sources[1].Artifact.Id;

        Assert.Equal(0, index.FindEntry(DiagnosticLocation.ForLine(artifact, 8)));   // between 5 and 9
        Assert.Equal(1, index.FindEntry(DiagnosticLocation.ForLine(artifact, 500))); // after the last
        Assert.Equal(-1, index.FindEntry(DiagnosticLocation.ForLine(artifact, 2)));  // before the first
    }

    [Fact]
    public void FindEntry_does_not_confuse_artifacts_or_kinds()
    {
        var index = Index(E(0, 1, 5), E(1, 2, 5));

        Assert.Equal(1, index.FindEntry(DiagnosticLocation.ForLine(index.Sources[2].Artifact.Id, 5)));
        Assert.Equal(-1, index.FindEntry(DiagnosticLocation.ForLine(Guid.NewGuid(), 5)));
        Assert.Equal(-1, index.FindEntry(DiagnosticLocation.ForEventRecord(index.Sources[1].Artifact.Id, 5)));
        Assert.Equal(-1, index.FindEntry(DiagnosticLocation.ForArtifact(index.Sources[1].Artifact.Id)));
        Assert.Equal(-1, index.FindEntry(DiagnosticLocation.ForRegistry(index.Sources[1].Artifact.Id, "HKLM\\X")));
    }

    [Fact]
    public void An_entry_is_small_enough_for_millions()
    {
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<TimelineEntry>());
    }
}
