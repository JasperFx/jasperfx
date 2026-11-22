using System.Linq;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

public class MappedNaturalKeyResolutionTests
{
    private readonly ResolutionGraph theGraph = new();

    [Fact]
    public void a_mapped_key_resolves_to_the_owned_key_of_its_source_aggregate()
    {
        var owner = new MnkOrderProjection();
        var summary = new MnkOrderSummaryProjection();
        theGraph.All.Add(owner);
        theGraph.All.Add(summary);

        theGraph.ResolveMappedNaturalKeys().ShouldBeEmpty();

        var mapped = summary.MappedNaturalKeyDefinitions.Single();
        mapped.SourceProjectionDefinition.ShouldBeSameAs(owner.NaturalKeyDefinition);
        mapped.LookupAggregateType.ShouldBe(typeof(MnkOrder));
    }

    [Fact]
    public void a_mapped_key_resolves_through_the_source_projection_class()
    {
        var owner = new MnkOrderProjection();
        var shipment = new MnkShipmentProjection();
        theGraph.All.Add(owner);
        theGraph.All.Add(shipment);

        theGraph.ResolveMappedNaturalKeys().ShouldBeEmpty();

        shipment.MappedNaturalKeyDefinitions.Single().SourceProjectionDefinition
            .ShouldBeSameAs(owner.NaturalKeyDefinition);
    }

    [Fact]
    public void a_mapped_key_resolves_against_a_key_its_source_inherits()
    {
        var owner = new MnkInheritedOrderProjection();
        var view = new MnkInheritedOrderViewProjection();
        theGraph.All.Add(owner);
        theGraph.All.Add(view);

        theGraph.ResolveMappedNaturalKeys().ShouldBeEmpty();

        view.MappedNaturalKeyDefinitions.Single().SourceProjectionDefinition
            .ShouldBeSameAs(owner.NaturalKeyDefinition);
    }

    [Fact]
    public void an_owned_key_inside_a_composite_projection_is_refused()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Orders");
        composite.StageFor(1).Add(new MnkOrderProjection());
        theGraph.All.Add(composite);

        var message = theGraph.FindOwnedNaturalKeysInComposites().ShouldHaveSingleItem();
        message.ShouldContain("EventTests.Projections.MnkOrder.Number is an owned natural key");
        message.ShouldContain("the composite projection 'Orders'");

        var ex = Should.Throw<InvalidProjectionException>(() => theGraph.AssertValidity(new object()));
        ex.Message.ShouldContain("cannot own a natural key");
    }

    [Fact]
    public void an_owned_key_inside_a_nested_composite_projection_is_refused()
    {
        var inner = new CompositeProjection<FakeOperations, FakeSession>("Inner");
        inner.StageFor(1).Add(new MnkOrderProjection());
        var outer = new CompositeProjection<FakeOperations, FakeSession>("Outer");
        outer.StageFor(1).Add(inner);
        theGraph.All.Add(outer);

        theGraph.FindOwnedNaturalKeysInComposites().ShouldHaveSingleItem()
            .ShouldContain("EventTests.Projections.MnkOrder.Number");
    }

    [Fact]
    public void a_mapped_key_inside_a_composite_projection_is_resolved()
    {
        var owner = new MnkOrderProjection();
        var summary = new MnkOrderSummaryProjection();
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Summaries");
        composite.StageFor(1).Add(summary);

        theGraph.All.Add(owner);
        theGraph.All.Add(composite);

        // A mapped key reads the lookup of an owner outside the composite, so it is allowed there
        theGraph.FindOwnedNaturalKeysInComposites().ShouldBeEmpty();
        theGraph.ResolveMappedNaturalKeys().ShouldBeEmpty();

        summary.MappedNaturalKeyDefinitions.Single().SourceProjectionDefinition
            .ShouldBeSameAs(owner.NaturalKeyDefinition);
    }

    [Fact]
    public void a_mapped_key_inside_a_nested_composite_projection_is_resolved()
    {
        var owner = new MnkOrderProjection();
        var summary = new MnkOrderSummaryProjection();
        var inner = new CompositeProjection<FakeOperations, FakeSession>("Inner");
        inner.StageFor(1).Add(summary);
        var outer = new CompositeProjection<FakeOperations, FakeSession>("Outer");
        outer.StageFor(1).Add(inner);

        theGraph.All.Add(owner);
        theGraph.All.Add(outer);

        theGraph.ResolveMappedNaturalKeys().ShouldBeEmpty();

        summary.MappedNaturalKeyDefinitions.Single().SourceProjectionDefinition
            .ShouldBeSameAs(owner.NaturalKeyDefinition);
    }

    [Fact]
    public void an_unregistered_source_is_reported()
    {
        theGraph.All.Add(new MnkOrderSummaryProjection());

        var message = theGraph.ResolveMappedNaturalKeys().ShouldHaveSingleItem();
        message.ShouldContain("MnkOrderSummary.Number");
        message.ShouldContain("no aggregate projection for that type is registered");
    }

    [Fact]
    public void a_source_without_an_owned_key_is_reported()
    {
        theGraph.All.Add(new MnkUnkeyedProjection());
        theGraph.All.Add(new MnkPointsAtUnkeyedProjection());

        theGraph.ResolveMappedNaturalKeys().ShouldHaveSingleItem().ShouldContain("does not own a natural key");
    }

    [Fact]
    public void a_source_whose_key_has_another_type_is_reported()
    {
        theGraph.All.Add(new MnkOrderProjection());
        theGraph.All.Add(new MnkWrongTypeProjection());

        theGraph.ResolveMappedNaturalKeys().ShouldHaveSingleItem()
            .ShouldContain("but its source EventTests.Projections.MnkOrder owns natural key Number of type EventTests.Projections.MnkOrderNumber");
    }

    [Fact]
    public void assert_validity_fails_on_an_unresolvable_mapped_key()
    {
        theGraph.All.Add(new MnkOrderSummaryProjection());

        var ex = Should.Throw<InvalidProjectionException>(() => theGraph.AssertValidity(new object()));
        ex.Message.ShouldContain("MnkOrderSummary.Number");
    }

    private sealed class ResolutionGraph : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public ResolutionGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }
}

public sealed record MnkUnkeyed
{
    public Guid Id { get; set; }
}

public class MnkUnkeyedProjection : SingleStreamProjection<MnkUnkeyed, Guid>
{
    public override MnkUnkeyed? Evolve(MnkUnkeyed? snapshot, Guid id, IEvent e) => snapshot;
}

// Deliberately invalid: the runtime checks are what these fixtures test.
#pragma warning disable JFXEVT013, JFXEVT015

public sealed record MnkPointsAtUnkeyed
{
    public Guid Id { get; set; }

    [MappedNaturalKey<MnkUnkeyed>]
    public MnkOrderNumber Number { get; set; } = default!;
}

public class MnkPointsAtUnkeyedProjection : SingleStreamProjection<MnkPointsAtUnkeyed, Guid>
{
    public override MnkPointsAtUnkeyed? Evolve(MnkPointsAtUnkeyed? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record MnkWrongType
{
    public Guid Id { get; set; }

    [MappedNaturalKey<MnkOrder>]
    public MnkCustomerCode Number { get; set; } = default!;
}

public class MnkWrongTypeProjection : SingleStreamProjection<MnkWrongType, Guid>
{
    public override MnkWrongType? Evolve(MnkWrongType? snapshot, Guid id, IEvent e) => snapshot;
}

#pragma warning restore JFXEVT013, JFXEVT015

// Maps to an aggregate whose natural key is declared on its base class
public sealed record MnkInheritedOrderView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<MnkInheritedOrder>]
    public MnkOrderNumber Number { get; set; } = default!;
}

public class MnkInheritedOrderViewProjection : SingleStreamProjection<MnkInheritedOrderView, Guid>
{
    public override MnkInheritedOrderView? Evolve(MnkInheritedOrderView? snapshot, Guid id, IEvent e) => snapshot;
}
