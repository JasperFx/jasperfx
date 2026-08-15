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

    // The string-keyed Append factories used to append straight to the backing list instead of
    // going through AddEvent, so the envelopes never got StreamKey/StreamId/TenantId. PrepareEvents
    // does not close the gap -- it sets TenantId, Timestamp, Version and Sequence, but never the
    // stream identity -- so an inline projection reading e.StreamKey silently wrote a blank field.
    // See #663, reported downstream as JasperFX/fisher#72.
    [Fact]
    public void append_by_stream_key_stamps_the_stream_identity_on_each_event()
    {
        var action = StreamAction.Append(theEvents, "purple", new AEvent(), new BEvent(), new CEvent());

        action.Events.Count.ShouldBe(3);
        foreach (var @event in action.Events)
        {
            @event.StreamKey.ShouldBe("purple");
            @event.Id.ShouldNotBe(Guid.Empty);
        }
    }

    [Fact]
    public void append_by_stream_key_with_built_events_stamps_identity_and_keeps_version_order()
    {
        var second = new Event<BEvent>(new BEvent()) { Version = 2 };
        var first = new Event<AEvent>(new AEvent()) { Version = 1 };

        var action = StreamAction.Append("purple", [second, first]);

        // ordering by version is the behaviour this overload had before, and has to survive
        action.Events.Select(x => x.Version).ToArray().ShouldBe([1L, 2L]);

        foreach (var @event in action.Events)
        {
            @event.StreamKey.ShouldBe("purple");
        }
    }

    [Fact]
    public void append_by_stream_key_propagates_the_tenant_id_to_the_events()
    {
        var action = StreamAction.Append(theEvents, "purple", new AEvent(), new BEvent());
        action.TenantId = "TX";

        foreach (var @event in action.Events)
        {
            @event.TenantId.ShouldBe("TX");
            @event.StreamKey.ShouldBe("purple");
        }
    }
}