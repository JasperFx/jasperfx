using EventStoreTests.TestingSupport;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Shouldly;

namespace EventStoreTests.Projections;

public class EventSliceTests
{
    [Fact]
    public void add_event_on_guid_no_identifer()
    {
        var slice = new EventSlice<SimpleAggregate, Guid>(Guid.NewGuid(),
            "foo");

        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamId.ShouldBe(slice.Id);
    }

    [Fact]
    public void add_event_on_string_no_identifer()
    {
        var slice = new EventSlice<SimpleAggregate, string>(Guid.NewGuid().ToString(),
            "foo");

        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamKey.ShouldBe(slice.Id);
    }

    [Fact]
    public void raise_event_on_supplied_guid_identifier()
    {
        var slice = new EventSlice<SimpleAggregate, string>(Guid.NewGuid().ToString(),
            "foo");

        var streamId = Guid.NewGuid();
        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(streamId, new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamId.ShouldBe(streamId);

    }

    [Fact]
    public void raise_event_on_supplied_string_identifier()
    {
        var slice = new EventSlice<SimpleAggregate, string>(Guid.NewGuid().ToString(),
            "foo");

        var streamKey = Guid.NewGuid().ToString();
        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(streamKey, new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamKey.ShouldBe(streamKey);

    }

    [Fact]
    public void fan_out()
    {
        var slice = new EventSlice<Trip, Guid>(Guid.NewGuid(), StorageConstants.DefaultTenantId);
        var e1 = slice.AddEventByData(new TripStarted());
        var e2 = slice.AddEventByData(Travel.Random(1));
        var e3 = slice.AddEventByData(new Arrival());
        var e4 = slice.AddEventByData(Travel.Random(2));
        var e5 = slice.AddEventByData(new Arrival());
        
        slice.FanOut<Travel, Movement>(x => x.Movements);
        
        slice.Events().ElementAt(0).ShouldBe(e1);
        slice.Events().ElementAt(1).ShouldBe(e2);

        for (int i = 0; i < e2.Data.Movements.Count; i++)
        {
            slice.Events().ElementAt(i + 2).ShouldBeOfType<Event<Movement>>();
        }

        var nextIndex = e2.Data.Movements.Count + 2;
        slice.Events().ElementAt(nextIndex).ShouldBe(e3);
    }
}
