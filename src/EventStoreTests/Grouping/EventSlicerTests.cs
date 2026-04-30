using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

public class EventSlicerTests
{
    [Fact]
    public async Task slice_by_single_identity()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);

        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Added(2, "green");
        var e3 = events.Added(3, "blue");
        var e4 = events.Subtracted(4, "green");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events.All, group);

        group.Slices["blue"].Events().ShouldBe([e1, e3]);
        group.Slices["green"].Events().ShouldBe([e2, e4]);
    }

    [Fact]
    public async Task slice_by_multiple_identities()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identities<ITaggedEvent>(e => e.Tags);

        var events = new TestEventSet();
        var e1 = events.Started("blue", "green");
        var e2 = events.Ended("red", "blue");
        var e3 = events.Started("green");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events.All, group);

        group.Slices["blue"].Events().ShouldBe([e1, e2]);
        group.Slices["green"].Events().ShouldBe([e1, e3]);
        group.Slices["red"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task slice_with_multiple_identity_rules()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);
        slicer.Identities<ITaggedEvent>(e => e.Tags);

        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Started("blue", "green");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events.All, group);

        // "blue" gets both the color event and the tagged event
        group.Slices["blue"].Events().ShouldBe([e1, e2]);
        group.Slices["green"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task slice_with_fan_out_after_grouping()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);
        slicer.FanOut<Added, int>(a => Enumerable.Range(0, a.Number), FanoutMode.AfterGrouping);

        var events = new TestEventSet();
        events.Added(3, "blue");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events.All, group);

        var blueEvents = group.Slices["blue"].Events().ToList();
        // Original Added event plus 3 fanned out int events
        blueEvents.Count.ShouldBe(4);
        blueEvents[0].Data.ShouldBeOfType<Added>();
        blueEvents[1].Data.ShouldBeOfType<int>();
        blueEvents[2].Data.ShouldBeOfType<int>();
        blueEvents[3].Data.ShouldBeOfType<int>();
    }

    [Fact]
    public async Task slice_with_fan_out_multiple_event_types()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);
        slicer.FanOut<Added, int>(a => Enumerable.Range(0, a.Number));
        slicer.FanOut<Subtracted, int>(s => Enumerable.Range(100, s.Number));

        var events = new TestEventSet();
        events.Added(2, "blue");
        events.Subtracted(3, "blue");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events.All, group);

        var blueEvents = group.Slices["blue"].Events().ToList();
        // Added(2 fan-out ints) + Subtracted(3 fan-out ints) = 2 originals + 5 ints
        blueEvents.Count.ShouldBe(7);
        blueEvents[0].Data.ShouldBeOfType<Added>();
        blueEvents[3].Data.ShouldBeOfType<Subtracted>();
    }

    [Fact]
    public void has_any_rules_returns_false_when_no_configuration()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.HasAnyRules().ShouldBeFalse();
    }

    [Fact]
    public void has_any_rules_returns_true_after_identity()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);
        slicer.HasAnyRules().ShouldBeTrue();
    }

    [Fact]
    public async Task slice_with_no_matching_events_produces_no_slices()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);

        // Only tagged events, no color events
        var events = new TestEventSet();
        events.Started("blue");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(events.All, group);

        group.Slices.Count().ShouldBe(0);
    }

    [Fact]
    public async Task slice_with_empty_events()
    {
        var slicer = new EventSlicer<SimpleAggregate, string>();
        slicer.Identity<IColorEvent>(e => e.Color);

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new List<IEvent>(), group);

        group.Slices.Count().ShouldBe(0);
    }
}

public class EventSlicerWithSessionTests
{
    [Fact]
    public async Task slice_by_single_identity()
    {
        var slicer = new EventSlicer<SimpleAggregate, string, object>();
        slicer.Identity<IColorEvent>(e => e.Color);

        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Added(2, "green");
        var e3 = events.Subtracted(3, "blue");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new object(), events.All, group);

        group.Slices["blue"].Events().ShouldBe([e1, e3]);
        group.Slices["green"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task slice_by_multiple_identities()
    {
        var slicer = new EventSlicer<SimpleAggregate, string, object>();
        slicer.Identities<ITaggedEvent>(e => e.Tags);

        var events = new TestEventSet();
        var e1 = events.Started("blue", "green");
        var e2 = events.Ended("red");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new object(), events.All, group);

        group.Slices["blue"].Events().ShouldBe([e1]);
        group.Slices["green"].Events().ShouldBe([e1]);
        group.Slices["red"].Events().ShouldBe([e2]);
    }

    [Fact]
    public async Task slice_with_custom_grouping()
    {
        var slicer = new EventSlicer<SimpleAggregate, string, object>();

        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Added(2, "green");
        
        slicer.CustomGrouping((session, page, grouping) =>
        {
            page.Count.ShouldBe(2);
            page[0].ShouldBe(e1);
            page[1].ShouldBe(e2);

            foreach (var e in page)
            {
                grouping.AddEvent("all", e);
            }
            return Task.CompletedTask;
        });

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new object(), events.All, group);

        group.Slices["all"].Events().ShouldBe([e1, e2]);
    }

    [Fact]
    public async Task slice_with_fan_out_after_grouping()
    {
        var slicer = new EventSlicer<SimpleAggregate, string, object>();
        slicer.Identity<IColorEvent>(e => e.Color);
        slicer.FanOut<Added, int>(a => Enumerable.Range(0, a.Number), FanoutMode.AfterGrouping);

        var events = new TestEventSet();
        events.Added(2, "red");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new object(), events.All, group);

        var redEvents = group.Slices["red"].Events().ToList();
        redEvents.Count.ShouldBe(3); // original + 2 fanned out
        redEvents[0].Data.ShouldBeOfType<Added>();
    }

    [Fact]
    public async Task slice_with_fan_out_before_grouping()
    {
        var slicer = new EventSlicer<SimpleAggregate, string, object>();
        slicer.FanOut<Added, Subtracted>(
            a => new[] { new Subtracted { Number = a.Number * 10, Color = a.Color } },
            FanoutMode.BeforeGrouping);
        slicer.Identity<IColorEvent>(e => e.Color);

        var events = new TestEventSet();
        events.Added(1, "green");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new object(), events.All, group);

        var greenEvents = group.Slices["green"].Events().ToList();
        greenEvents.Count.ShouldBe(2);
        greenEvents[0].Data.ShouldBeOfType<Added>();
        greenEvents[1].Data.ShouldBeOfType<Subtracted>();
    }

    [Fact]
    public async Task slice_combines_identity_and_custom_grouping()
    {
        var slicer = new EventSlicer<SimpleAggregate, string, object>();
        slicer.Identity<IColorEvent>(e => e.Color);
        slicer.CustomGrouping((session, events, grouping) =>
        {
            // Add all events to a "summary" slice
            foreach (var e in events)
            {
                grouping.AddEvent("summary", e);
            }
            return Task.CompletedTask;
        });

        var events = new TestEventSet();
        var e1 = events.Added(1, "blue");
        var e2 = events.Added(2, "green");

        var group = new SliceGroup<SimpleAggregate, string>();
        await slicer.SliceAsync(new object(), events.All, group);

        // Identity rule routes by color
        group.Slices["blue"].Events().ShouldBe([e1]);
        group.Slices["green"].Events().ShouldBe([e2]);
        // Custom grouping adds everything to "summary"
        group.Slices["summary"].Events().ShouldBe([e1, e2]);
    }
}
