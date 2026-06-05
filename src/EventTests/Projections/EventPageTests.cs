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
}
