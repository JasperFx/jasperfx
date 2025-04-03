using JasperFx.Events;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class EventRangeTests
{
    [Fact]
    public void size_with_no_events()
    {
        var range = new EventRange(new ShardName("name"), 0, 100);
        range.Size.ShouldBe(100);
    }

    [Fact]
    public void size_with_events()
    {
        var range = new EventRange(new ShardName("name"), 0, 100)
        {
            Events = new List<IEvent>
            {
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent())
            }
        };

        range.Size.ShouldBe(5);
    }

    [Fact]
    public void skip_event_sequence()
    {
        var range = new EventRange(new ShardName("name"), 0, 100)
        {
            Events = new List<IEvent>
            {
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent()),
                new Event<AEvent>(new AEvent())
            }
        };

        var sequence = 111;
        foreach (var @event in range.Events) @event.Sequence = sequence++;

        range.SkipEventSequence(114);

        range.Events.Count.ShouldBe(4);
    }
}
