using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventTests.Grouping;

public class SliceGroupTests
{
    [Fact]
    public void just_add_events()
    {
        var events = new TestEventSet();
        events.Added(1, "blue");
        events.Added(2, "blue");
        events.Added(3, "blue");

        var group = new SliceGroup<SimpleAggregate, Guid>();
        var streamId = Guid.NewGuid();
        group.AddEvents(streamId, events.All);
        
        group.Slices[streamId].Events().ShouldBe(events.All);
    }

    [Fact]
    public void do_not_add_event_if_the_id_is_default()
    {
        var events = new TestEventSet();
        events.Added(1, "blue");
        events.Added(2, "blue");
        events.Added(3, "blue");
        var group = new SliceGroup<SimpleAggregate, Guid>();

        group.AddEvents(Guid.Empty, events.All);
        
        group.Slices.Any().ShouldBeFalse();
        
        group.AddEvent(Guid.Empty, events.All.First());
        
        group.Slices.Any().ShouldBeFalse();
    }

    [Fact]
    public void sort_by_event_type()
    {
        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Added(2, "green");
        var e3 = events.Added(3, "red");
        var e4 = events.Added(4, "blue");
        var e5 = events.Added(5, "red");
        var e6 = events.Added(6, "green");

        var group = new SliceGroup<SimpleAggregate, string>();
        group.AddEvents<IColorEvent>(e => e.Color, events.All);
        
        group.Slices["blue"].Events().ShouldBe([e1, e4]);
        group.Slices["green"].Events().ShouldBe([e2, e6]);
        group.Slices["red"].Events().ShouldBe([e3, e5]);
    }

    [Fact]
    public void sort_by_metadata_and_type()
    {
        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Added(2, "green");
        var e3 = events.Added(3, "red");
        var e4 = events.Added(4, "blue");
        var e5 = events.Added(5, "red");
        var e6 = events.Added(6, "green");

        foreach (var e in events.All)
        {
            e.StreamKey = "a";
        }
        
        var group = new SliceGroup<SimpleAggregate, string>();
        group.AddEvents<IEvent<IColorEvent>>(e => $"{e.StreamKey}:{e.Data.Color}", events.All);
        
        group.Slices["a:blue"].Events().ShouldBe([e1, e4]);
        group.Slices["a:green"].Events().ShouldBe([e2, e6]);
        group.Slices["a:red"].Events().ShouldBe([e3, e5]);
    }

    [Fact]
    public void slice_by_many_identities_by_data_type()
    {
        var events = new TestEventSet();
        var e1 = events.Started("blue", "green");
        var e2 = events.Started("blue", "purple");
        var e3 = events.Ended("orange", "purple");
        var e4 = events.Ended("blue", "pink");
        var e5 = events.Started("pink", "green");
        var e6 = events.Started("orange", "blue", "pink");
        
        var group = new SliceGroup<SimpleAggregate, string>();
        group.AddEvents<ITaggedEvent>(e => e.Tags, events.All);
        
        group.Slices["blue"].Events().ShouldBe([e1, e2, e4, e6]);
        group.Slices["green"].Events().ShouldBe([e1, e5]);
        group.Slices["pink"].Events().ShouldBe([e4, e5, e6]);
    }
    
    [Fact]
    public void slice_by_many_identities_by_event_type()
    {
        var events = new TestEventSet();
        var e1 = events.Started("blue", "green");
        var e2 = events.Started("blue", "purple");
        var e3 = events.Ended("orange", "purple");
        var e4 = events.Ended("blue", "pink");
        var e5 = events.Started("pink", "green");
        var e6 = events.Started("orange", "blue", "pink");
        
        var group = new SliceGroup<SimpleAggregate, string>();
        group.AddEvents<IEvent<ITaggedEvent>>(e => e.Data.Tags, events.All);
        
        group.Slices["blue"].Events().ShouldBe([e1, e2, e4, e6]);
        group.Slices["green"].Events().ShouldBe([e1, e5]);
        group.Slices["pink"].Events().ShouldBe([e4, e5, e6]);
    }
    
    
}

public class TestEventSet
{
    public int Sequence = 0;
    
    public List<IEvent> All { get; } = new();

    public IEvent Added(int number, string color)
    {
        var added = new Added { Number = number, Color = color };
        var e = new Event<Added>(added);
        e.Sequence = ++Sequence;
        All.Add(e);
        return e;
    }
    
    public IEvent Subtracted(int number, string color)
    {
        var added = new Subtracted() { Number = number, Color = color };
        var e = new Event<Subtracted>(added);
        e.Sequence = ++Sequence;
        All.Add(e);
        return e;
    }

    public IEvent Started(params string[] tags)
    {
        var started = new Started { Tags = tags };
        var e = new Event<Started>(started);
        e.Sequence = ++Sequence;
        All.Add(e);
        return e;
    }
    
    public IEvent Ended(params string[] tags)
    {
        var started = new Ended { Tags = tags };
        var e = new Event<Ended>(started);
        e.Sequence = ++Sequence;
        All.Add(e);
        return e;
    }
}

public interface IColorEvent
{
    string Color { get; }
}

public interface ITaggedEvent
{
    string[] Tags { get; }
}

public class Added : IColorEvent
{
    public int Number { get; set; }
    public string Color { get; set; }
}

public class Subtracted : IColorEvent
{
    public int Number { get; set; }
    public string Color { get; set; }
}

public class Started : ITaggedEvent
{
    public string[] Tags { get; set; }
}

public class Ended : ITaggedEvent
{
    public string[] Tags { get; set; }
}