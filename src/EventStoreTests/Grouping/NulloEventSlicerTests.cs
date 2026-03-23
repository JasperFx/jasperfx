using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

public class NulloEventSlicerTests
{
    [Fact]
    public async Task returns_all_events_as_single_group()
    {
        var slicer = new NulloEventSlicer();

        var events = new List<IEvent>
        {
            new Event<AEvent>(new AEvent()) { Sequence = 1 },
            new Event<BEvent>(new BEvent()) { Sequence = 2 },
            new Event<CEvent>(new CEvent()) { Sequence = 3 },
        };

        var groups = await slicer.SliceAsync(events);

        groups.Count.ShouldBe(1);
        groups[0].ShouldBe(events);
    }

    [Fact]
    public async Task empty_events_returns_single_empty_group()
    {
        var slicer = new NulloEventSlicer();

        var events = new List<IEvent>();
        var groups = await slicer.SliceAsync(events);

        groups.Count.ShouldBe(1);
        ((IReadOnlyList<IEvent>)groups[0]).Count.ShouldBe(0);
    }
}
