using JasperFx.Events;
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
        // Last() on the empty page; it should fall back to the high water mark.
        var page = new EventPage(0);

        Should.NotThrow(() => page.CalculateCeiling(10, 1000, skippedEvents: 10));

        page.Ceiling.ShouldBe(1000);
    }

    [Fact]
    public void an_entirely_skipped_full_batch_claims_only_what_the_query_read()
    {
        // https://github.com/JasperFx/jasperfx/issues/667. Ten rows read, ten skipped, so the
        // batch was saturated and the LIMIT stopped the query at sequence 10. Everything between
        // 11 and the high water mark was never looked at, and the consumer writes Ceiling as
        // durable projection progress, so claiming 1000 here skips those events permanently.
        var page = new EventPage(0) { LastObservedSequence = 10 };

        page.CalculateCeiling(10, 1000, skippedEvents: 10);

        page.Ceiling.ShouldBe(10);
    }

    [Fact]
    public void an_entirely_skipped_full_batch_moves_past_the_poison_rows_without_passing_the_query()
    {
        // The reason the high water mark fallback exists at all is liveness: a ceiling stuck at
        // the floor leaves the shard re-reading the same undeserializable rows forever. The fix
        // has to keep that while dropping the over-claim, so both bounds are asserted together.
        //
        // Written with an exact value rather than only ShouldBeGreaterThan(Floor). A range
        // assertion here would pass against the very behaviour this pins, since the old ceiling of
        // 1000 is also greater than the floor, and a test that passes against the bug it describes
        // is not a test.
        var page = new EventPage(floor: 5) { LastObservedSequence = 15 };

        page.CalculateCeiling(10, 1000, skippedEvents: 10);

        page.Ceiling.ShouldBe(15);
        page.Ceiling.ShouldBeGreaterThan(page.Floor);
    }

    [Fact]
    public void a_partly_skipped_full_batch_still_uses_its_last_surviving_event()
    {
        // LastObservedSequence must not take over whenever it happens to be set. While the page
        // holds anything, the last event in it is the ceiling, exactly as before: those rows were
        // read and kept, and the ones after them inside the batch were read and skipped.
        var page = new EventPage(0)
        {
            new Event<AEvent>(new AEvent()) { Sequence = 4 }
        };
        page.LastObservedSequence = 9;

        page.CalculateCeiling(10, 1000, skippedEvents: 9);

        page.Ceiling.ShouldBe(4);
    }

    [Fact]
    public void an_unfilled_batch_ignores_the_last_observed_sequence()
    {
        // A batch that did not fill genuinely exhausted its range, so the high water mark is
        // correct and must win even when the loader reported what it last read. Without this,
        // a loader that always sets the property would stop advancing past sparse ranges.
        var page = new EventPage(0) { LastObservedSequence = 7 };

        page.CalculateCeiling(10, 1000, skippedEvents: 2);

        page.Ceiling.ShouldBe(1000);
    }
}
