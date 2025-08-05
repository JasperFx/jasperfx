using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

public class EventRangeTests
{
    [Fact]
    public void size_with_no_events()
    {
        var range = new EventRange(new ShardName("name"), 0, 100, Substitute.For<ISubscriptionAgent>());
        range.Size.ShouldBe(100);
    }

    [Fact]
    public void size_with_events()
    {
        var range = new EventRange(new ShardName("name"), 0, 100, Substitute.For<ISubscriptionAgent>())
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
    public async Task skip_event_sequence()
    {
        var subscriptionAgent = Substitute.For<ISubscriptionAgent>();
        var range = new EventRange(new ShardName("name"), 0, 100, subscriptionAgent)
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

        await range.SkipEventSequence(114);

        subscriptionAgent.Received().MarkSkipped(114);

        range.Events.Count.ShouldBe(4);
    }
}
