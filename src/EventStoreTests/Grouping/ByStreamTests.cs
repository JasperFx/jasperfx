using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

public class ByStreamTests_with_guid_identity
{
    [Fact]
    public async Task slices_events_by_stream_id()
    {
        var slicer = new ByStream<SimpleAggregate, Guid>();

        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        var e1 = new Event<AEvent>(new AEvent()) { StreamId = stream1, Sequence = 1 };
        var e2 = new Event<BEvent>(new BEvent()) { StreamId = stream2, Sequence = 2 };
        var e3 = new Event<AEvent>(new AEvent()) { StreamId = stream1, Sequence = 3 };
        var e4 = new Event<CEvent>(new CEvent()) { StreamId = stream2, Sequence = 4 };

        var events = new List<IEvent> { e1, e2, e3, e4 };

        var group = new SliceGroup<SimpleAggregate, Guid>();
        await slicer.SliceAsync(events, group);

        group.Slices[stream1].Events().ShouldBe([e1, e3]);
        group.Slices[stream2].Events().ShouldBe([e2, e4]);
    }

    [Fact]
    public async Task single_stream_produces_single_slice()
    {
        var slicer = new ByStream<SimpleAggregate, Guid>();
        var streamId = Guid.NewGuid();

        var e1 = new Event<AEvent>(new AEvent()) { StreamId = streamId, Sequence = 1 };
        var e2 = new Event<BEvent>(new BEvent()) { StreamId = streamId, Sequence = 2 };

        var group = new SliceGroup<SimpleAggregate, Guid>();
        await slicer.SliceAsync(new List<IEvent> { e1, e2 }, group);

        group.Slices.Count().ShouldBe(1);
        group.Slices[streamId].Events().ShouldBe([e1, e2]);
    }

    [Fact]
    public async Task empty_events_produces_no_slices()
    {
        var slicer = new ByStream<SimpleAggregate, Guid>();

        var group = new SliceGroup<SimpleAggregate, Guid>();
        await slicer.SliceAsync(new List<IEvent>(), group);

        group.Slices.Count().ShouldBe(0);
    }

    [Fact]
    public void implements_ISingleStreamSlicer()
    {
        var slicer = new ByStream<SimpleAggregate, Guid>();
        slicer.ShouldBeAssignableTo<ISingleStreamSlicer>();
    }
}

public class ByStreamTests_with_string_identity
{
    [Fact]
    public async Task slices_events_by_stream_key()
    {
        var slicer = new ByStream<SimpleAggregate, string>();

        var e1 = new Event<AEvent>(new AEvent()) { StreamKey = "stream-a", Sequence = 1 };
        var e2 = new Event<BEvent>(new BEvent()) { StreamKey = "stream-b", Sequence = 2 };
        var e3 = new Event<AEvent>(new AEvent()) { StreamKey = "stream-a", Sequence = 3 };

        var events = new List<IEvent> { e1, e2, e3 };

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events, group);

        group.Slices["stream-a"].Events().ShouldBe([e1, e3]);
        group.Slices["stream-b"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task many_streams_each_get_own_slice()
    {
        var slicer = new ByStream<SimpleAggregate, string>();

        var events = new List<IEvent>();
        for (int i = 0; i < 5; i++)
        {
            events.Add(new Event<AEvent>(new AEvent()) { StreamKey = $"stream-{i}", Sequence = i + 1 });
        }

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events, group);

        group.Slices.Count().ShouldBe(5);
        for (int i = 0; i < 5; i++)
        {
            group.Slices[$"stream-{i}"].Events().Count.ShouldBe(1);
        }
    }
}
