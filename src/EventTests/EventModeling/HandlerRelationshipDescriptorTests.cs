using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

public class HandlerRelationshipDescriptorTests
{
    private static readonly TypeDescriptor Handler = TypeDescriptor.For(typeof(PlaceOrderHandler));
    private static readonly TypeDescriptor Command = TypeDescriptor.For(typeof(PlaceOrder));
    private static readonly TypeDescriptor Aggregate = TypeDescriptor.For(typeof(Order));
    private static readonly TypeDescriptor PlacedEvent = TypeDescriptor.For(typeof(OrderPlaced));
    private static readonly TypeDescriptor ShippedEvent = TypeDescriptor.For(typeof(OrderShipped));

    [Fact]
    public void positional_ctor_round_trips_inputs()
    {
        var descriptor = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            new[] { PlacedEvent, ShippedEvent },
            Aggregate);

        descriptor.HandlerType.ShouldBe(Handler);
        descriptor.MessageType.ShouldBe(Command);
        descriptor.EmittedEvents.Count.ShouldBe(2);
        descriptor.EmittedEvents[0].ShouldBe(PlacedEvent);
        descriptor.EmittedEvents[1].ShouldBe(ShippedEvent);
        descriptor.TargetAggregate.ShouldBe(Aggregate);
    }

    [Fact]
    public void target_aggregate_is_optional()
    {
        // Stateless handlers don't load an aggregate snapshot; the generator
        // emits null for TargetAggregate so the swim-lane knows not to draw
        // the aggregate arrow.
        var descriptor = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            new[] { PlacedEvent },
            TargetAggregate: null);

        descriptor.TargetAggregate.ShouldBeNull();
    }

    [Fact]
    public void supports_empty_emitted_events_list()
    {
        // Handlers that don't emit events still have a useful relationship
        // entry — the swim-lane renders the command-to-handler edge even
        // when no events come back.
        var descriptor = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            Array.Empty<TypeDescriptor>(),
            TargetAggregate: null);

        descriptor.EmittedEvents.ShouldBeEmpty();
    }

    private record Order;
    private record PlaceOrder;
    private record OrderPlaced;
    private record OrderShipped;
    private record PlaceOrderHandler;
}
