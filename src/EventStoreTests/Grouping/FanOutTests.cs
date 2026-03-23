using EventStoreTests.TestingSupport;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using Shouldly;
using EventListFanOut = JasperFx.Events.Grouping.EventListExtensions;

namespace EventStoreTests.Grouping;

public class FanOutTests
{
    [Fact]
    public void fan_out_on_event_data_inserts_children_after_parent()
    {
        var travel = Travel.Random(1);
        var events = new List<IEvent>
        {
            new Event<TripStarted>(new TripStarted()) { Sequence = 1 },
            new Event<Travel>(travel) { Sequence = 2 },
            new Event<Arrival>(new Arrival()) { Sequence = 3 },
        };

        EventListFanOut.FanOut<Travel, Movement>(events, t => t.Movements);

        // Original events + fanned out movements
        events.Count.ShouldBe(3 + travel.Movements.Count);

        events[0].Data.ShouldBeOfType<TripStarted>();
        events[1].Data.ShouldBeOfType<Travel>();

        // Movements should be inserted right after the Travel event
        for (int i = 0; i < travel.Movements.Count; i++)
        {
            events[i + 2].Data.ShouldBeOfType<Movement>();
            ((Movement)events[i + 2].Data).ShouldBe(travel.Movements[i]);
        }

        // Arrival should be at the end
        events.Last().Data.ShouldBeOfType<Arrival>();
    }

    [Fact]
    public void fan_out_on_event_data_with_multiple_parents()
    {
        var travel1 = Travel.Random(1);
        var travel2 = Travel.Random(2);
        var events = new List<IEvent>
        {
            new Event<Travel>(travel1) { Sequence = 1 },
            new Event<Travel>(travel2) { Sequence = 2 },
        };

        EventListFanOut.FanOut<Travel, Movement>(events, t => t.Movements);

        events.Count.ShouldBe(2 + travel1.Movements.Count + travel2.Movements.Count);

        // First travel + its movements
        events[0].Data.ShouldBeOfType<Travel>();
        for (int i = 0; i < travel1.Movements.Count; i++)
        {
            ((Movement)events[i + 1].Data).ShouldBe(travel1.Movements[i]);
        }

        // Second travel + its movements
        var secondTravelIndex = 1 + travel1.Movements.Count;
        events[secondTravelIndex].Data.ShouldBeOfType<Travel>();
        for (int i = 0; i < travel2.Movements.Count; i++)
        {
            ((Movement)events[secondTravelIndex + 1 + i].Data).ShouldBe(travel2.Movements[i]);
        }
    }

    [Fact]
    public void fan_out_with_no_matching_source_events()
    {
        var events = new List<IEvent>
        {
            new Event<AEvent>(new AEvent()) { Sequence = 1 },
            new Event<BEvent>(new BEvent()) { Sequence = 2 },
        };

        EventListFanOut.FanOut<Travel, Movement>(events, t => t.Movements);

        // No Travel events, so nothing should change
        events.Count.ShouldBe(2);
    }

    [Fact]
    public void fan_out_with_empty_children()
    {
        var travel = new Travel { Day = 1 }; // No movements added
        travel.Movements.Clear();
        var events = new List<IEvent>
        {
            new Event<Travel>(travel) { Sequence = 1 },
        };

        EventListFanOut.FanOut<Travel, Movement>(events, t => t.Movements);

        // Just the Travel event, no children
        events.Count.ShouldBe(1);
    }

    [Fact]
    public void fan_out_data_operator_applies_to_event_list()
    {
        var fanOut = new FanOutEventDataOperator<Travel, Stop>(t => t.Stops);

        var travel = Travel.Random(1);
        var events = new List<IEvent>
        {
            new Event<Travel>(travel) { Sequence = 1 },
        };

        var result = fanOut.Apply(events);

        result.Count.ShouldBe(1 + travel.Stops.Count);
        result[0].Data.ShouldBeOfType<Travel>();
        for (int i = 0; i < travel.Stops.Count; i++)
        {
            result[i + 1].Data.ShouldBeOfType<Stop>();
        }
    }

    [Fact]
    public void fan_out_data_operator_originating_type()
    {
        var fanOut = new FanOutEventDataOperator<Travel, Stop>(t => t.Stops);
        fanOut.OriginatingType.ShouldBe(typeof(Travel));
    }

    [Fact]
    public void fan_out_event_operator_originating_type()
    {
        var fanOut = new FanOutEventOperator<Travel, Stop>(e => e.Data.Stops);
        fanOut.OriginatingType.ShouldBe(typeof(Travel));
    }

    [Fact]
    public void fan_out_event_operator_applies_to_event_list()
    {
        var fanOut = new FanOutEventOperator<Travel, Stop>(e => e.Data.Stops);

        var travel = Travel.Random(1);
        var events = new List<IEvent>
        {
            new Event<Travel>(travel) { Sequence = 1 },
        };

        var result = fanOut.Apply(events);

        result.Count.ShouldBe(1 + travel.Stops.Count);
    }

    [Fact]
    public async Task fan_out_on_slice_group()
    {
        var travel = Travel.Random(1);
        var streamId = Guid.NewGuid();

        var group = new SliceGroup<Trip, Guid>();
        group.AddEvent(streamId, new Event<TripStarted>(new TripStarted()) { Sequence = 1 });
        group.AddEvent(streamId, new Event<Travel>(travel) { Sequence = 2 });
        group.AddEvent(streamId, new Event<Arrival>(new Arrival()) { Sequence = 3 });

        group.FanOutOnEach<Travel, Movement>(t => t.Movements);

        var events = group.Slices[streamId].Events().ToList();
        events.Count.ShouldBe(3 + travel.Movements.Count);
        events[0].Data.ShouldBeOfType<TripStarted>();
        events[1].Data.ShouldBeOfType<Travel>();
        events.Last().Data.ShouldBeOfType<Arrival>();
    }

    [Fact]
    public async Task fan_out_across_multiple_slices()
    {
        var travel1 = Travel.Random(1);
        var travel2 = Travel.Random(2);
        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        var group = new SliceGroup<Trip, Guid>();
        group.AddEvent(stream1, new Event<Travel>(travel1) { Sequence = 1 });
        group.AddEvent(stream2, new Event<Travel>(travel2) { Sequence = 2 });

        group.FanOutOnEach<Travel, Movement>(t => t.Movements);

        group.Slices[stream1].Events().Count.ShouldBe(1 + travel1.Movements.Count);
        group.Slices[stream2].Events().Count.ShouldBe(1 + travel2.Movements.Count);
    }

    [Fact]
    public async Task apply_fan_out_rules_to_slice_group()
    {
        var travel = Travel.Random(1);
        var streamId = Guid.NewGuid();

        var group = new SliceGroup<Trip, Guid>();
        group.AddEvent(streamId, new Event<Travel>(travel) { Sequence = 1 });

        var rules = new List<IFanOutRule>
        {
            new FanOutEventDataOperator<Travel, Movement>(t => t.Movements),
            new FanOutEventDataOperator<Travel, Stop>(t => t.Stops),
        };

        group.ApplyFanOutRules(rules);

        var events = group.Slices[streamId].Events().ToList();
        // Travel + Movements (after Travel) + Stops (after Travel, but Travel is first element)
        // The fan-out rules are applied sequentially, so movements are inserted after Travel,
        // then stops are inserted after Travel (which is still at index 0)
        events[0].Data.ShouldBeOfType<Travel>();

        var movementCount = events.Count(e => e.Data is Movement);
        var stopCount = events.Count(e => e.Data is Stop);

        movementCount.ShouldBe(travel.Movements.Count);
        stopCount.ShouldBe(travel.Stops.Count);
    }
}
