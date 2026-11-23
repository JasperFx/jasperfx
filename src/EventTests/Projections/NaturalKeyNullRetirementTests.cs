using System.Linq;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

/// <summary>
/// jasperfx#1287 (marten#6153): only an explicit key source retires a stream's key with a null. A source discovery
/// inferred means "this event carried no key" when it yields null.
/// </summary>
public class NaturalKeyNullRetirementTests
{
    [Fact]
    public void a_static_source_returning_the_key_retires_the_key_on_null()
    {
        var mapping = new NnkNullableSourceProjection().NaturalKeyDefinition!.EventMappings.ShouldHaveSingleItem();

        mapping.RetiresKeyOnNull.ShouldBeTrue();
    }

    [Fact]
    public void a_key_read_off_the_event_does_not_retire_the_key_on_null()
    {
        var mapping = new NnkReadOffEventProjection().NaturalKeyDefinition!.EventMappings.ShouldHaveSingleItem();

        mapping.RetiresKeyOnNull.ShouldBeFalse();
    }

    [Fact]
    public void an_apply_run_against_a_blank_aggregate_does_not_retire_the_key_on_null()
    {
        var mapping = new NkInstanceMethodProjection().NaturalKeyDefinition!.EventMappings.ShouldHaveSingleItem();

        mapping.RetiresKeyOnNull.ShouldBeFalse();
    }

    [Fact]
    public void an_explicit_registration_retires_the_key_on_null()
    {
        var projection = new NkEvolvingKeyProjection();
        projection.NaturalKeyFor(x => x.SetBy<NkCreatedEvent>(e => new NkAggregateKey(e.Key)));

        projection.NaturalKeyDefinition!.EventMappings
            .Single(m => m.EventType == typeof(NkCreatedEvent))
            .RetiresKeyOnNull.ShouldBeTrue();
    }

    [Fact]
    public void a_mapping_built_without_the_flag_retires_the_key_on_null_as_before()
    {
        new NaturalKeyEventMapping(typeof(NkCreatedEvent), _ => null).RetiresKeyOnNull.ShouldBeTrue();
    }

    [Fact]
    public void a_refusal_whose_problems_each_name_their_fix_leaves_out_the_general_advice()
    {
        var ex = Should.Throw<InvalidProjectionException>(
            () => new NksPrivateSourceProjection().AssembleAndAssertValidity());

        ex.Message.ShouldContain("it is not public");
        ex.Message.ShouldNotContain("Either give the method a signature");
    }
}
