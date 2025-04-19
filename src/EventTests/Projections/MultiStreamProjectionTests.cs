using EventTests.TestingSupport;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

public class MultiStreamProjectionTests
{
    [Fact]
    public void all_good_projection()
    {
        new DayProjection().AssembleAndAssertValidity();
    }

    [Fact]
    public void throw_invalid_projection_if_no_slicing()
    {
        Should.Throw<InvalidProjectionException>(() =>
        {
            new NoSlicerDayProjection().AssembleAndAssertValidity();
        });
    }

    [Fact]
    public void all_good_with_custom_slicer()
    {
        new UsesCustomSlicerDayProjection().AssembleAndAssertValidity();
    }

    [Fact]
    public void good_slicer_no_methods()
    {
        Should.Throw<InvalidProjectionException>(() =>
        {
            new SlicesButNoMethodsDayProjection().AssembleAndAssertValidity();
        });
    }
}

public class Day
{
    public long Version { get; set; }

    public int Id { get; set; }

    // how many trips started on this day?
    public int Started { get; set; }

    // how many trips ended on this day?
    public int Ended { get; set; }

    public int Stops { get; set; }

    // how many miles did the active trips
    // drive in which direction on this day?
    public double North { get; set; }
    public double East { get; set; }
    public double West { get; set; }
    public double South { get; set; }
}

public class UsesCustomSlicerDayProjection : JasperFxMultiStreamProjectionBase<Day, int, FakeOperations, FakeSession>
{
    public UsesCustomSlicerDayProjection() : base([])
    {
        CustomGrouping(Substitute.For<IEventSlicer<Day, int, FakeSession>>());
    }
    
    public void Apply(Day day, TripStarted e)
    {
        day.Started++;
    }

    public void Apply(Day day, TripEnded e)
    {
        day.Ended++;
    }
}

public class NoSlicerDayProjection : JasperFxMultiStreamProjectionBase<Day, int, FakeOperations, FakeSession>
{
    public NoSlicerDayProjection() : base([])
    {
    }
    
    public void Apply(Day day, TripStarted e)
    {
        day.Started++;
    }

    public void Apply(Day day, TripEnded e)
    {
        day.Ended++;
    }
}

public class DayProjection: JasperFxMultiStreamProjectionBase<Day, int, FakeOperations, FakeSession>
{
    public DayProjection() : base([])
    {
        // Tell the projection how to group the events
        // by Day document
        Identity<IDayEvent>(x => x.Day);

        // This just lets the projection work independently
        // on each Movement child of the Travel event
        // as if it were its own event
        FanOut<Travel, Movement>(x => x.Movements);

        // You can also access Event data
        FanOut<Travel, Stop>(x => x.Data.Stops);

        ((ProjectionBase)this).Name = "Day";

        // Opt into 2nd level caching of up to 100
        // most recently encountered aggregates as a
        // performance optimization
        Options.CacheLimitPerTenant = 1000;

        // With large event stores of relatively small
        // event objects, moving this number up from the
        // default can greatly improve throughput and especially
        // improve projection rebuild times
        Options.BatchSize = 5000;
    }

    public void Apply(Day day, TripStarted e)
    {
        day.Started++;
    }

    public void Apply(Day day, TripEnded e)
    {
        day.Ended++;
    }

    public void Apply(Day day, Movement e)
    {
        switch (e.Direction)
        {
            case Direction.East:
                day.East += e.Distance;
                break;
            case Direction.North:
                day.North += e.Distance;
                break;
            case Direction.South:
                day.South += e.Distance;
                break;
            case Direction.West:
                day.West += e.Distance;
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void Apply(Day day, Stop e)
    {
        day.Stops++;
    }
}

public class SlicesButNoMethodsDayProjection: JasperFxMultiStreamProjectionBase<Day, int, FakeOperations, FakeSession>
{
    public SlicesButNoMethodsDayProjection() : base([])
    {
        // Tell the projection how to group the events
        // by Day document
        Identity<IDayEvent>(x => x.Day);

        // This just lets the projection work independently
        // on each Movement child of the Travel event
        // as if it were its own event
        FanOut<Travel, Movement>(x => x.Movements);

        // You can also access Event data
        FanOut<Travel, Stop>(x => x.Data.Stops);

        ((ProjectionBase)this).Name = "Day";

        // Opt into 2nd level caching of up to 100
        // most recently encountered aggregates as a
        // performance optimization
        Options.CacheLimitPerTenant = 1000;

        // With large event stores of relatively small
        // event objects, moving this number up from the
        // default can greatly improve throughput and especially
        // improve projection rebuild times
        Options.BatchSize = 5000;
    }

}

