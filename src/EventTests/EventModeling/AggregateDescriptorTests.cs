using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

public class AggregateDescriptorTests
{
    private static readonly TypeDescriptor OrderType = TypeDescriptor.For(typeof(Order));
    private static readonly TypeDescriptor PlacedEvent = TypeDescriptor.For(typeof(OrderPlaced));
    private static readonly TypeDescriptor ShippedEvent = TypeDescriptor.For(typeof(OrderShipped));

    [Fact]
    public void positional_ctor_round_trips_inputs()
    {
        var descriptor = new AggregateDescriptor(
            OrderType,
            AggregateKind.WriteAggregate,
            new[] { PlacedEvent, ShippedEvent });

        descriptor.Type.ShouldBe(OrderType);
        descriptor.Kind.ShouldBe(AggregateKind.WriteAggregate);
        descriptor.AppliedEvents.Count.ShouldBe(2);
        descriptor.AppliedEvents[0].ShouldBe(PlacedEvent);
        descriptor.AppliedEvents[1].ShouldBe(ShippedEvent);
    }

    [Fact]
    public void supports_empty_applied_events_list()
    {
        // The CW#144 generator emits an empty list when it can't statically
        // resolve the apply set — the swim-lane renders this as "no events"
        // rather than failing.
        var descriptor = new AggregateDescriptor(
            OrderType,
            AggregateKind.BoundaryModel,
            Array.Empty<TypeDescriptor>());

        descriptor.AppliedEvents.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AggregateKind.WriteAggregate)]
    [InlineData(AggregateKind.ReadAggregate)]
    [InlineData(AggregateKind.ConsistentAggregate)]
    [InlineData(AggregateKind.BoundaryModel)]
    public void accepts_all_four_kinds(AggregateKind kind)
    {
        var descriptor = new AggregateDescriptor(OrderType, kind, Array.Empty<TypeDescriptor>());
        descriptor.Kind.ShouldBe(kind);
    }

    [Fact]
    public void records_have_structural_equality()
    {
        var a = new AggregateDescriptor(OrderType, AggregateKind.WriteAggregate, new[] { PlacedEvent });
        var b = new AggregateDescriptor(OrderType, AggregateKind.WriteAggregate, new[] { PlacedEvent });

        // Records get structural equality on positional members for free.
        // The collection-typed property compares by reference, not element
        // sequence — pin the documented behavior so consumers aren't
        // surprised by `Equals` returning false on equal-by-content lists.
        a.ShouldBe(a);
        a.Equals(b).ShouldBeFalse(); // different list instances
    }

    private record Order;
    private record OrderPlaced;
    private record OrderShipped;
}
