using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class EventPageTests
{
    [Fact]
    public void calculate_ceiling_when_page_is_full_uses_last_sequence()
    {
        var page = new EventPage(0)
        {
            new Event<AEvent>(new AEvent()) { Sequence = 4 },
            new Event<AEvent>(new AEvent()) { Sequence = 5 }
        };

        page.CalculateCeiling(2, 1000);

        page.Ceiling.ShouldBe(5);
    }

    [Fact]
    public void calculate_ceiling_when_page_is_not_full_uses_high_water_mark()
    {
        var page = new EventPage(0)
        {
            new Event<AEvent>(new AEvent()) { Sequence = 4 }
        };

        page.CalculateCeiling(10, 1000);

        page.Ceiling.ShouldBe(1000);
    }

    [Fact]
    public void calculate_ceiling_when_full_batch_was_entirely_skipped_does_not_throw()
    {
        // Reproduces https://github.com/JasperFx/marten/issues/4663 -- every event in a
        // full batch was skipped, so the page is empty. CalculateCeiling must not call
        // Last() on the empty page.
        var page = new EventPage(0);

        Should.NotThrow(() => page.CalculateCeiling(10, 1000, skippedEvents: 10));
    }

    /// <remarks>
    /// jasperfx#667. The batch was saturated, so the query stopped at its LIMIT rather than exhausting
    /// the range -- rows between sequence 10 and the high water mark at 1000 were never read. Claiming
    /// 1000 would write durable progress over them.
    /// </remarks>
    [Fact]
    public void calculate_ceiling_when_full_batch_was_entirely_skipped_stops_at_the_last_row_observed()
    {
        var page = new EventPage(0);
        foreach (var sequence in Enumerable.Range(1, 10))
        {
            page.RecordSkippedEvent(sequence);
        }

        page.CalculateCeiling(10, 1000, skippedEvents: 10);

        page.Ceiling.ShouldBe(10);
    }

    /// <remarks>
    /// The counterpart to the case above, and the reason the fix keys off saturation rather than off the
    /// page being empty: here the query really did exhaust its range, so everything up to the high water
    /// mark is genuinely accounted for even though nothing was kept.
    /// </remarks>
    [Fact]
    public void calculate_ceiling_when_a_partial_batch_was_entirely_skipped_claims_the_high_water_mark()
    {
        var page = new EventPage(0);
        page.RecordSkippedEvent(3);
        page.RecordSkippedEvent(4);

        page.CalculateCeiling(10, 1000, skippedEvents: 2);

        page.Ceiling.ShouldBe(1000);
    }

    /// <remarks>
    /// A loader that counts skips without reporting their sequences keeps the pre-jasperfx#667 behavior.
    /// The page has no sequence to stop at and stalling at the floor would re-read the poison rows
    /// forever, so liveness wins -- see marten#4663.
    /// </remarks>
    [Fact]
    public void calculate_ceiling_falls_back_to_the_high_water_mark_when_no_skipped_sequence_was_recorded()
    {
        var page = new EventPage(0);

        page.CalculateCeiling(10, 1000, skippedEvents: 10);

        page.Ceiling.ShouldBe(1000);
    }

    /// <remarks>
    /// A skip after the last kept event still moves the ceiling: the row was read and deliberately
    /// dropped, so progress past it is earned. Stopping at the last kept sequence instead would re-read
    /// and re-skip the tail of every such page.
    /// </remarks>
    [Fact]
    public void calculate_ceiling_uses_a_trailing_skip_past_the_last_kept_event()
    {
        var page = new EventPage(0)
        {
            new Event<AEvent>(new AEvent()) { Sequence = 7 }
        };
        page.RecordSkippedEvent(8);
        page.RecordSkippedEvent(9);

        page.CalculateCeiling(3, 1000, skippedEvents: 2);

        page.Ceiling.ShouldBe(9);
    }

    [Fact]
    public void calculate_ceiling_keeps_the_last_kept_event_when_the_skips_came_earlier()
    {
        var page = new EventPage(0)
        {
            new Event<AEvent>(new AEvent()) { Sequence = 8 },
            new Event<AEvent>(new AEvent()) { Sequence = 9 }
        };
        page.RecordSkippedEvent(7);

        page.CalculateCeiling(3, 1000, skippedEvents: 1);

        page.Ceiling.ShouldBe(9);
    }

    [Fact]
    public void calculate_ceiling_over_an_empty_range_claims_the_high_water_mark()
    {
        var page = new EventPage(0);

        page.CalculateCeiling(10, 1000);

        page.Ceiling.ShouldBe(1000);
    }

    [Fact]
    public void record_skipped_event_keeps_the_maximum_regardless_of_call_order()
    {
        var page = new EventPage(0);
        page.RecordSkippedEvent(9);
        page.RecordSkippedEvent(4);

        page.LastSkippedSequence.ShouldBe(9);
    }

    /// <remarks>
    /// Marten's UnknownEventTypeException uses -1 for "the sequence could not be determined". Recording
    /// it would place the ceiling below the floor, so it has to be ignored -- the page then falls back to
    /// the high water mark, which is the pre-jasperfx#667 behavior rather than a new failure mode.
    /// </remarks>
    [Fact]
    public void record_skipped_event_ignores_an_undetermined_sequence_sentinel()
    {
        var page = new EventPage(100);
        page.RecordSkippedEvent(-1);

        page.LastSkippedSequence.ShouldBe(0);

        page.CalculateCeiling(1, 1000, skippedEvents: 1);
        page.Ceiling.ShouldBe(1000);
    }

    [Fact]
    public void record_skipped_event_takes_the_sequence_from_a_failure_context()
    {
        var page = new EventPage(0);
        page.RecordSkippedEvent(new StubFailureContext(42));

        page.LastSkippedSequence.ShouldBe(42);
    }

    /// <summary>
    /// Stands in for a store's skippable read failure — Marten's UnknownEventTypeException and
    /// EventDeserializationFailureException both implement this interface.
    /// </summary>
    private class StubFailureContext(long sequence): IEventFailureContext
    {
        public ShardFailureCategory Category => ShardFailureCategory.UnknownEventType;
        public long Sequence { get; } = sequence;
        public string? EventTypeName => "stub";
        public Guid? EventId => null;
        public Guid? StreamId => null;
        public string? StreamKey => null;
        public string? TenantId => null;
        public long? Version => null;
    }
}
