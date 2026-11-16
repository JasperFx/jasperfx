using EventTests.TestingSupport;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

// Identity<T> and Identities<T> slice T, so T has to be on the projection's event filter: with no types on
// the filter the async daemon fetches every event in the store, and with other types on it T never arrives.
public class multi_stream_identity_event_filter
{
    [Fact]
    public void identity_and_identities_put_their_event_types_on_an_otherwise_empty_filter()
    {
        var projection = new IdentityOnlyDayProjection();
        projection.AssembleAndAssertValidity();

        projection.IncludedEventTypes.ShouldBe([typeof(TripStarted), typeof(TripEnded)], ignoreOrder: true);
    }

    [Fact]
    public void identity_adds_its_event_type_next_to_types_that_are_already_on_the_filter()
    {
        var projection = new IdentityNextToIncludedTypeDayProjection();
        projection.AssembleAndAssertValidity();

        projection.IncludedEventTypes.ShouldBe([typeof(TripAborted), typeof(TripStarted)], ignoreOrder: true);
    }

    [Fact]
    public void identity_over_the_event_wrapper_includes_the_wrapped_event_type()
    {
        var projection = new WrapperIdentityDayProjection();
        projection.AssembleAndAssertValidity();

        projection.IncludedEventTypes.ShouldBe([typeof(TripStarted), typeof(TripEnded)], ignoreOrder: true);
    }

    [Fact]
    public void identity_on_an_interface_leaves_the_filter_alone()
    {
        // An interface or abstract type on the filter makes Marten drop the type filter altogether, so
        // including IDayEvent would turn this filtered projection into one that reads every event.
        var projection = new DayProjection();
        projection.AssembleAndAssertValidity();

        projection.IncludedEventTypes.ShouldNotContain(typeof(IDayEvent));
        projection.IncludedEventTypes.ShouldAllBe(x => !x.IsInterface && !x.IsAbstract);
    }

    [Fact]
    public void identity_over_every_event_leaves_the_filter_alone()
    {
        var projection = new EveryEventIdentityDayProjection();
        projection.AssembleAndAssertValidity();

        projection.IncludedEventTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_custom_grouping_leaves_an_empty_filter_empty()
    {
        var projection = new CustomGroupingOnlyDayProjection();
        projection.AssembleAndAssertValidity();

        projection.IncludedEventTypes.ShouldBeEmpty();
    }
}

public abstract class EvolvingDayProjection : JasperFxMultiStreamProjectionBase<Day, int, FakeOperations, FakeSession>
{
    public override Day? Evolve(Day? snapshot, int id, IEvent e)
    {
        snapshot ??= new Day { Id = id };
        snapshot.Started++;
        return snapshot;
    }
}

public class IdentityOnlyDayProjection : EvolvingDayProjection
{
    public IdentityOnlyDayProjection()
    {
        Identity<TripStarted>(x => x.Day);
        Identities<TripEnded>(x => [x.Day]);
    }
}

public class IdentityNextToIncludedTypeDayProjection : EvolvingDayProjection
{
    public IdentityNextToIncludedTypeDayProjection()
    {
        IncludeType<TripAborted>();
        Identity<TripStarted>(x => x.Day);
    }
}

public class WrapperIdentityDayProjection : EvolvingDayProjection
{
    public WrapperIdentityDayProjection()
    {
        Identity<IEvent<TripStarted>>(x => x.Data.Day);
        Identities<IEvent<TripEnded>>(x => [x.Data.Day]);
    }
}

public class EveryEventIdentityDayProjection : EvolvingDayProjection
{
    public EveryEventIdentityDayProjection()
    {
        Identity<IEvent>(x => x.Version > 0 ? 1 : 0);
    }
}

public class CustomGroupingOnlyDayProjection : EvolvingDayProjection
{
    public CustomGroupingOnlyDayProjection()
    {
        CustomGrouping((_, _, _) => Task.CompletedTask);
    }
}
