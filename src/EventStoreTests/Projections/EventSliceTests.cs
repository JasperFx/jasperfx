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
}
