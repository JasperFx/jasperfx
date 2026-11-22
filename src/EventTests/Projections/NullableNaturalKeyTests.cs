using System.Linq;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

/// <summary>
/// A natural key whose type is a struct may be declared as <c>TKey?</c> on the aggregate, on an event, or as
/// the return type of a <c>[NaturalKeySource]</c> method. All of them are the same key type, <c>TKey</c>.
/// </summary>
public class NullableNaturalKeyTests
{
    [Fact]
    public void a_nullable_struct_wrapper_key_is_its_underlying_type()
    {
        var definition = new NaturalKeyDefinition(typeof(NnkNullableOwner),
            typeof(NnkNullableOwner).GetProperty(nameof(NnkNullableOwner.Code))!);

        definition.OuterType.ShouldBe(typeof(NnkCode));
        definition.InnerType.ShouldBe(typeof(string));
        definition.IsNullable.ShouldBeTrue();
        definition.IsValid().ShouldBeTrue();

        definition.Unwrap(new NnkCode("A-1")).ShouldBe("A-1");
        definition.Unwrap((NnkCode?)new NnkCode("A-2")).ShouldBe("A-2");
        definition.Unwrap(null).ShouldBeNull();
    }

    [Fact]
    public void reads_the_unwrapped_key_value_off_an_aggregate()
    {
        var definition = new NnkNullableOwnerProjection().NaturalKeyDefinition!;

        definition.ReadValue(new NnkNullableOwner { Code = new NnkCode("R-1") }).ShouldBe("R-1");
        definition.ReadValue(new NnkNullableOwner { Code = null }).ShouldBeNull();
    }

    [Fact]
    public void a_nullable_primitive_key_is_its_underlying_type()
    {
        var definition = new NaturalKeyDefinition(typeof(NnkNullableNumber),
            typeof(NnkNullableNumber).GetProperty(nameof(NnkNullableNumber.Number))!);

        definition.OuterType.ShouldBe(typeof(int));
        definition.InnerType.ShouldBe(typeof(int));
        definition.IsNullable.ShouldBeTrue();
        definition.IsValid().ShouldBeTrue();
        definition.Unwrap((int?)42).ShouldBe(42);
    }

    [Fact]
    public void a_non_nullable_key_is_not_nullable()
    {
        var definition = new MnkOrderProjection().NaturalKeyDefinition!;
        definition.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void a_source_returning_the_key_binds_to_a_nullable_key_property()
    {
        var definition = new NnkNullableOwnerProjection().NaturalKeyDefinition!;

        definition.DiscoveryProblems.ShouldBeEmpty();
        var mapping = definition.EventMappings.ShouldHaveSingleItem();
        mapping.Extractor(new Event<NnkCodeAssigned>(new NnkCodeAssigned(new NnkCode("A-3"))))
            .ShouldBe(new NnkCode("A-3"));
    }

    [Fact]
    public void a_source_returning_a_nullable_key_binds_to_a_key_property()
    {
        var definition = new NnkNullableSourceProjection().NaturalKeyDefinition!;

        definition.DiscoveryProblems.ShouldBeEmpty();
        var mapping = definition.EventMappings.ShouldHaveSingleItem();
        mapping.Extractor(new Event<NnkMaybeCodeAssigned>(new NnkMaybeCodeAssigned(new NnkCode("A-4"))))
            .ShouldBe(new NnkCode("A-4"));
        mapping.Extractor(new Event<NnkMaybeCodeAssigned>(new NnkMaybeCodeAssigned(null))).ShouldBeNull();
    }

    [Fact]
    public void a_nullable_key_on_the_event_body_is_read_for_a_key_property()
    {
        // No usable source method of its own: the key is read straight off the event's NnkCode? property
        var definition = new NnkReadOffEventProjection().NaturalKeyDefinition!;

        definition.DiscoveryProblems.ShouldBeEmpty();
        var mapping = definition.EventMappings.ShouldHaveSingleItem();
        mapping.Extractor(new Event<NnkMaybeCodeAssigned>(new NnkMaybeCodeAssigned(new NnkCode("A-5"))))
            .ShouldBe(new NnkCode("A-5"));
    }

    [Fact]
    public void a_key_and_its_nullable_form_on_one_aggregate_are_the_same_type()
    {
        var ex = Should.Throw<InvalidProjectionException>(
            () => new NnkSameTypeTwiceProjection().AssembleAndAssertValidity());

        ex.Message.ShouldContain("distinct type");
    }

    [Fact]
    public void a_nullable_mapped_key_resolves_against_a_non_nullable_owned_key()
    {
        var graph = new NullableGraph();
        var owner = new NnkNullableSourceProjection();
        var view = new NnkNullableViewProjection();
        graph.All.Add(owner);
        graph.All.Add(view);

        graph.ResolveMappedNaturalKeys().ShouldBeEmpty();
        view.MappedNaturalKeyDefinitions.Single().SourceProjectionDefinition.ShouldBeSameAs(owner.NaturalKeyDefinition);
    }

    private sealed class NullableGraph : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public NullableGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }
}

// ───────────────────────── fixtures ─────────────────────────

public readonly record struct NnkCode(string Value);

public record NnkCodeAssigned(NnkCode Code);

public record NnkMaybeCodeAssigned(NnkCode? Code);

public sealed record NnkNullableOwner
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NnkCode? Code { get; set; }

    [NaturalKeySource]
    public static NnkCode KeyFor(NnkCodeAssigned e) => e.Code;
}

public class NnkNullableOwnerProjection : SingleStreamProjection<NnkNullableOwner, Guid>
{
    public override NnkNullableOwner? Evolve(NnkNullableOwner? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record NnkNullableSource
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NnkCode Code { get; set; }

    [NaturalKeySource]
    public static NnkCode? KeyFor(NnkMaybeCodeAssigned e) => e.Code;
}

public class NnkNullableSourceProjection : SingleStreamProjection<NnkNullableSource, Guid>
{
    public override NnkNullableSource? Evolve(NnkNullableSource? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record NnkReadOffEvent
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NnkCode Code { get; set; }

    // Neither static nor returning the key, and it needs the aggregate: discovery falls back to reading
    // the key off the event body.
    [NaturalKeySource]
    public static NnkReadOffEvent Apply(NnkMaybeCodeAssigned e, NnkReadOffEvent current) =>
        current with { Code = e.Code ?? current.Code };

    public NnkReadOffEvent(Guid id) => Id = id;
}

public class NnkReadOffEventProjection : SingleStreamProjection<NnkReadOffEvent, Guid>
{
    public override NnkReadOffEvent? Evolve(NnkReadOffEvent? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record NnkNullableNumber
{
    public Guid Id { get; set; }

    [NaturalKey]
    public int? Number { get; set; }
}

public sealed record NnkNullableView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<NnkNullableSource>]
    public NnkCode? Code { get; set; }
}

public class NnkNullableViewProjection : SingleStreamProjection<NnkNullableView, Guid>
{
    public override NnkNullableView? Evolve(NnkNullableView? snapshot, Guid id, IEvent e) => snapshot;
}

#pragma warning disable JFXEVT011

public sealed record NnkSameTypeTwice
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NnkCode Own { get; set; }

    [MappedNaturalKey<NnkNullableSource>]
    public NnkCode? Borrowed { get; set; }
}

public class NnkSameTypeTwiceProjection : SingleStreamProjection<NnkSameTypeTwice, Guid>
{
    public override NnkSameTypeTwice? Evolve(NnkSameTypeTwice? snapshot, Guid id, IEvent e) => snapshot;
}

#pragma warning restore JFXEVT011
