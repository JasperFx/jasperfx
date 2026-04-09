using EventStoreTests.TestingSupport;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;

namespace EventStoreTests.Grouping;

/// <summary>
/// Tests for composite slicer scenarios — TenantedEventSlicer wrapping EventSlicer,
/// multi-type fan-out with identity routing, and complex DayProjection-style patterns.
/// Addresses gaps identified in JasperFx/jasperfx#60.
/// </summary>
public class CompositeSlicerTests
{
    [Fact]
    public async Task tenanted_slicer_with_custom_identity_slicer()
    {
        // Compose: TenantedEventSlicer wrapping an EventSlicer that routes by Day
        var inner = new EventSlicer<DayView, int>();
        inner.Identity<IDayEvent>(e => e.Day);

        var slicer = new TenantedEventSlicer<DayView, int>(inner);

        var events = new List<IEvent>
        {
            new Event<TripStarted>(new TripStarted { Day = 1 }) { Sequence = 1, TenantId = "tenant-a" },
            new Event<TripStarted>(new TripStarted { Day = 2 }) { Sequence = 2, TenantId = "tenant-a" },
            new Event<TripStarted>(new TripStarted { Day = 1 }) { Sequence = 3, TenantId = "tenant-b" },
            new Event<TripEnded>(new TripEnded { Day = 2 }) { Sequence = 4, TenantId = "tenant-b" },
        };

        var results = await slicer.SliceAsync(events);

        // Should produce two SliceGroups (one per tenant)
        results.Count.ShouldBe(2);

        var groupA = results.OfType<SliceGroup<DayView, int>>().First(g => g.TenantId == "tenant-a");
        groupA.Slices.Count.ShouldBe(2);
        groupA.Slices[1].Events().Count.ShouldBe(1);
        groupA.Slices[2].Events().Count.ShouldBe(1);

        var groupB = results.OfType<SliceGroup<DayView, int>>().First(g => g.TenantId == "tenant-b");
        groupB.Slices.Count.ShouldBe(2);
        groupB.Slices[1].Events().Count.ShouldBe(1);
        groupB.Slices[2].Events().Count.ShouldBe(1);
    }

    [Fact]
    public async Task slicer_with_fan_out_after_grouping()
    {
        // Fan-out happens AFTER grouping, so children land in the same slice as their parent.
        // Route by Day via IDayEvent, then fan-out Travel → Movement within each slice.
        var slicer = new EventSlicer<DayView, int>();
        slicer.Identity<IDayEvent>(e => e.Day);
        slicer.FanOut<Travel, Movement>(t => t.Movements, FanoutMode.AfterGrouping);
        slicer.FanOut<Travel, Stop>(t => t.Stops, FanoutMode.AfterGrouping);

        var travel1 = Travel.Random(1);
        var travel2 = Travel.Random(2);
        var events = new List<IEvent>
        {
            new Event<TripStarted>(new TripStarted { Day = 1 }) { Sequence = 1 },
            new Event<Travel>(travel1) { Sequence = 2 },
            new Event<TripEnded>(new TripEnded { Day = 1 }) { Sequence = 3 },
            new Event<Travel>(travel2) { Sequence = 4 },
        };

        var group = new SliceGroup<DayView, int>();
        await slicer.SliceAsync(events, group);

        // Day 1: TripStarted + Travel1 + fanned-out Movements + fanned-out Stops + TripEnded
        var day1Events = group.Slices[1].Events();
        day1Events.ShouldContain(e => e.Data is TripStarted);
        day1Events.ShouldContain(e => e.Data is Travel);
        day1Events.ShouldContain(e => e.Data is TripEnded);
        day1Events.Select(e => e.Data).OfType<Movement>().Count().ShouldBe(travel1.Movements.Count);
        day1Events.Select(e => e.Data).OfType<Stop>().Count().ShouldBe(travel1.Stops.Count);

        // Day 2: Travel2 + fanned-out children
        var day2Events = group.Slices[2].Events();
        day2Events.ShouldContain(e => e.Data is Travel);
        day2Events.Select(e => e.Data).OfType<Movement>().Count().ShouldBe(travel2.Movements.Count);
        day2Events.Select(e => e.Data).OfType<Stop>().Count().ShouldBe(travel2.Stops.Count);
    }

    [Fact]
    public async Task slicer_with_combined_identity_and_identities()
    {
        var slicer = new EventSlicer<DayView, int>();
        slicer.Identity<IDayEvent>(e => e.Day);
        slicer.Identities<MultiDayEvent>(e => e.Days);

        var events = new List<IEvent>
        {
            new Event<TripStarted>(new TripStarted { Day = 1 }) { Sequence = 1 },
            new Event<MultiDayEvent>(new MultiDayEvent { Days = [1, 2, 3] }) { Sequence = 2 },
            new Event<TripEnded>(new TripEnded { Day = 3 }) { Sequence = 3 },
        };

        var group = new SliceGroup<DayView, int>();
        await slicer.SliceAsync(events, group);

        // Day 1: TripStarted + MultiDayEvent
        group.Slices[1].Events().Count.ShouldBe(2);

        // Day 2: MultiDayEvent only
        group.Slices[2].Events().Count.ShouldBe(1);

        // Day 3: MultiDayEvent + TripEnded
        group.Slices[3].Events().Count.ShouldBe(2);
    }

    [Fact]
    public async Task fan_out_stops_after_travel_events()
    {
        // Verify fan-out inserts children in correct position relative to parent
        var travel = Travel.Random(1);
        var events = new List<IEvent>
        {
            new Event<TripStarted>(new TripStarted { Day = 1 }) { Sequence = 1 },
            new Event<Travel>(travel) { Sequence = 2 },
            new Event<TripEnded>(new TripEnded { Day = 1 }) { Sequence = 3 },
        };

        JasperFx.Events.Grouping.EventListExtensions.FanOut<Travel, Stop>(events, t => t.Stops);

        // Verify ordering: TripStarted, Travel, [Stops...], TripEnded
        events[0].Data.ShouldBeOfType<TripStarted>();
        events[1].Data.ShouldBeOfType<Travel>();

        for (int i = 0; i < travel.Stops.Count; i++)
        {
            events[2 + i].Data.ShouldBeOfType<Stop>();
            ((Stop)events[2 + i].Data).ShouldBe(travel.Stops[i]);
        }

        events.Last().Data.ShouldBeOfType<TripEnded>();
    }

    [Fact]
    public async Task identity_slicer_ignores_unrecognized_events()
    {
        var slicer = new EventSlicer<DayView, int>();
        slicer.Identity<IDayEvent>(e => e.Day);

        var events = new List<IEvent>
        {
            new Event<TripStarted>(new TripStarted { Day = 1 }) { Sequence = 1 },
            new Event<Arrival>(new Arrival()) { Sequence = 2 }, // No IDayEvent, should be ignored
            new Event<TripEnded>(new TripEnded { Day = 1 }) { Sequence = 3 },
        };

        var group = new SliceGroup<DayView, int>();
        await slicer.SliceAsync(events, group);

        // Only day 1 should have events (TripStarted + TripEnded), Arrival is unrouted
        group.Slices[1].Events().Count.ShouldBe(2);
        group.Slices.Count.ShouldBe(1);
    }
}

// Test models
public class DayView
{
    public int Id { get; set; }
    public int TripCount { get; set; }
}

public class MultiDayEvent
{
    public int[] Days { get; set; } = [];
}
