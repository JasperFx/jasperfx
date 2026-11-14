using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1184: a [BoundaryAggregate] that also carries an Id. Marten fetches every boundary aggregate
// through SingleStreamProjection<T, string>, but the generator keyed the evolver on the Id's type, so
// FetchForWritingByTags<T> found no dispatcher. These are compiled by the real generator.
public record Bug1184ClassScheduled(int Capacity);
public record Bug1184SpotBooked;

[BoundaryAggregate]
public class Bug1184ClassState
{
    public Guid Id { get; set; }
    public int Capacity { get; set; }
    public int Booked { get; set; }

    public void Apply(Bug1184ClassScheduled e) => Capacity = e.Capacity;
    public void Apply(Bug1184SpotBooked e) => Booked++;
}

// Not a boundary aggregate: registering it with an identity type its Id does not have is a plain
// mismatch, and the message should say so rather than claim the generator declined the type.
public class Bug1184GuidKeyedState
{
    public Guid Id { get; set; }
    public int Booked { get; set; }

    public void Apply(Bug1184SpotBooked e) => Booked++;
}

public class Bug_1184_boundary_aggregate_with_an_id
{
    private static async Task<TDoc?> fold<TDoc, TId>(SingleStreamProjection<TDoc, TId> projection, TId id,
        params object[] events)
        where TDoc : class where TId : notnull
    {
        projection.AssembleAndAssertValidity();

        var (snapshot, _) = await projection.DetermineActionAsync(
            new FakeSession(),
            null,
            id,
            new NulloIdentitySetter<TDoc, TId>(),
            events.Select(x => (IEvent)Activator.CreateInstance(typeof(Event<>).MakeGenericType(x.GetType()), x)!)
                .ToList(),
            CancellationToken.None);

        return snapshot;
    }

    [Fact]
    public async Task the_string_keyed_projection_the_dcb_surface_builds_finds_a_dispatcher()
    {
        var state = await fold(new SingleStreamProjection<Bug1184ClassState, string>(), "boundary",
            new Bug1184ClassScheduled(12), new Bug1184SpotBooked(), new Bug1184SpotBooked());

        state.ShouldNotBeNull();
        state.Capacity.ShouldBe(12);
        state.Booked.ShouldBe(2);
    }

    [Fact]
    public async Task the_projection_keyed_on_the_id_member_still_finds_a_dispatcher()
    {
        var state = await fold(new SingleStreamProjection<Bug1184ClassState, Guid>(), Guid.NewGuid(),
            new Bug1184ClassScheduled(5), new Bug1184SpotBooked());

        state.ShouldNotBeNull();
        state.Capacity.ShouldBe(5);
        state.Booked.ShouldBe(1);
    }

    [Fact]
    public void a_dispatcher_keyed_on_another_identity_type_is_named_in_the_message()
    {
        var projection = new SingleStreamProjection<Bug1184GuidKeyedState, string>();

        var message = Should.Throw<InvalidProjectionException>(() => projection.AssembleAndAssertValidity())
            .Message;

        message.ShouldContain("No source-generated dispatcher found");
        message.ShouldContain("DID emit an evolver for EventTests.Projections.Bug1184GuidKeyedState");
        message.ShouldContain("keyed on the identity type System.Guid");

        // The misleading half: the generator did not decline the type, it keyed it differently.
        message.ShouldNotContain("declined it");
        message.ShouldNotContain("[BoundaryAggregate]");
    }
}
