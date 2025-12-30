using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

public class EventRangeTests
{
    [Fact]
    public void default_batch_behavior_is_individual()
    {
        var range = new EventRange(new ShardName("name"), 0, 100, Substitute.For<ISubscriptionAgent>());
        range.BatchBehavior.ShouldBe(BatchBehavior.Individual);
    }
    
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

    [Fact]
    public void clone()
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

        range.BatchBehavior = BatchBehavior.Composite;
        range.ActiveBatch = Substitute.For<IProjectionBatch>();

        var leafName = new ShardName("Bar", "All", 1);
        var cloned = range.CloneForExecutionLeaf(leafName);
        
        cloned.ShardName.ShouldBe(leafName);
        cloned.SequenceFloor.ShouldBe(range.SequenceFloor);
        cloned.SequenceCeiling.ShouldBe(range.SequenceCeiling);
        cloned.Agent.ShouldBe(range.Agent);
        cloned.BatchBehavior.ShouldBe(range.BatchBehavior);
        cloned.ActiveBatch.ShouldBe(range.ActiveBatch);
        cloned.Events.ShouldBe(range.Events);
    }

    [Fact]
    public void mark_updated()
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

        var workflowStatus = new WorkflowStatus(Guid.NewGuid());
        range.MarkUpdated("foo", workflowStatus);
        var e = range.Updates.Single().ShouldBeOfType<Updated<WorkflowStatus>>();
        e
            .Entity.ShouldBe(workflowStatus);
        
        e.TenantId.ShouldBe("foo");
    }
}

public record WorkflowStatus(Guid Id);
