using System.Collections;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;

namespace EventTests;

public class StreamActionTests
{
    private readonly EventRegistry theEvents;
    private readonly IMetadataContext theSession;

    public StreamActionTests()
    {
        theSession = Substitute.For<IMetadataContext>();
        theSession.TenantId.Returns("TX");

        theEvents = new EventRegistry();
    }

    [Fact]
    public void for_determines_action_type_guid()
    {
        var events = new List<IEvent>
        {
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent())
        };

        events[0].Version = 5;

        StreamAction.For(Guid.NewGuid(), events)
            .ActionType.ShouldBe(StreamActionType.Append);

        events[0].Version = 1;

        StreamAction.For(Guid.NewGuid(), events)
            .ActionType.ShouldBe(StreamActionType.Start);
    }

    [Fact]
    public void for_determines_action_type_string()
    {
        var events = new List<IEvent>
        {
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent()),
            new Event<AEvent>(new AEvent())
        };

        events[0].Version = 5;

        StreamAction.For(Guid.NewGuid().ToString(), events)
            .ActionType.ShouldBe(StreamActionType.Append);

        events[0].Version = 1;

        StreamAction.For(Guid.NewGuid().ToString(), events)
            .ActionType.ShouldBe(StreamActionType.Start);
    }

    [Fact]
    public void ApplyServerVersion_for_new_streams()
    {
        var action = StreamAction.Start(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());

        var queue = new Queue<long>();
        queue.Enqueue(11);
        queue.Enqueue(12);
        queue.Enqueue(13);
        queue.Enqueue(14);
        action.PrepareEvents(0, theEvents, queue, theSession);


        action.Events[0].Version.ShouldBe(1);
        action.Events[1].Version.ShouldBe(2);
        action.Events[2].Version.ShouldBe(3);
        action.Events[3].Version.ShouldBe(4);

        action.Events[0].Sequence.ShouldBe(11);
        action.Events[1].Sequence.ShouldBe(12);
        action.Events[2].Sequence.ShouldBe(13);
        action.Events[3].Sequence.ShouldBe(14);
    }


    [Fact]
    public void ApplyServerVersion_for_existing_streams()
    {
        var action = StreamAction.Append(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());

        var queue = new Queue<long>();
        queue.Enqueue(11);
        queue.Enqueue(12);
        queue.Enqueue(13);
        queue.Enqueue(14);


        action.PrepareEvents(5, theEvents, queue, theSession);

        action.ExpectedVersionOnServer.ShouldBe(5);


        action.Events[0].Version.ShouldBe(6);
        action.Events[1].Version.ShouldBe(7);
        action.Events[2].Version.ShouldBe(8);
        action.Events[3].Version.ShouldBe(9);
    }

    [Fact]
    public void is_starting_with_start_action_type()
    {
        var action = StreamAction.Start(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());
        
        action.IsStarting().ShouldBeTrue();
    }

    [Fact]
    public void is_not_starting_with_append()
    {
        var action = StreamAction.Append(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());

        action.Events[0].Version = 3;
            
        action.IsStarting().ShouldBeFalse();
    }

    [Fact]
    public void is_starting_event_with_append_action_if_the_first_version_is_1()
    {
        var action = StreamAction.Append(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());

        action.Events[0].Version = 1;
            
        action.IsStarting().ShouldBeTrue();
    }

    [Fact]
    public void overwrite_timestamp_on_selected_events_rich_append()
    {
        var currentTime = DateTime.Today;
        theEvents.TimeProvider = new FakeTimeProvider(currentTime);
        theEvents.AppendMode = EventAppendMode.Rich;
        
        var action = StreamAction.Append(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());

        action.Events[0].Timestamp = currentTime.Subtract(1.Hours());
        
        var queue = new Queue<long>();
        queue.Enqueue(10);
        queue.Enqueue(11);
        queue.Enqueue(12);
        queue.Enqueue(13);
        
        action.PrepareEvents(5, theEvents, queue, theSession);
    }
}