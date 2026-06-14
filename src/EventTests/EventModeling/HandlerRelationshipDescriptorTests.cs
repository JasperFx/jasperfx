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

    [Fact]
    public void kind_defaults_to_handler_and_origin_is_null()
    {
        // The historical positional-constructor shape must keep meaning
        // "a handler triggered by MessageType" so existing emitted manifests
        // round-trip unchanged after the 2.11 broadening.
        var descriptor = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            new[] { PlacedEvent },
            Aggregate);

        descriptor.Kind.ShouldBe(PublisherKind.Handler);
        descriptor.Origin.ShouldBeNull();
    }

    [Fact]
    public void handler_is_the_default_enum_member()
    {
        // Handler must be the zero value so default-initialized / deserialized
        // descriptors land on the historical behavior.
        ((int)PublisherKind.Handler).ShouldBe(0);
    }

    [Fact]
    public void can_describe_an_http_endpoint_publisher()
    {
        var descriptor = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            new[] { PlacedEvent },
            TargetAggregate: null)
        {
            Kind = PublisherKind.HttpEndpoint,
            Origin = new PublisherOrigin
            {
                HttpRoute = "/orders/{id}",
                HttpMethod = "POST",
                Label = "POST /orders/{id}"
            }
        };

        descriptor.Kind.ShouldBe(PublisherKind.HttpEndpoint);
        descriptor.Origin.ShouldNotBeNull();
        descriptor.Origin!.HttpRoute.ShouldBe("/orders/{id}");
        descriptor.Origin.HttpMethod.ShouldBe("POST");
        descriptor.Origin.Label.ShouldBe("POST /orders/{id}");
        // The positional inputs are still intact alongside the new metadata.
        descriptor.HandlerType.ShouldBe(Handler);
        descriptor.EmittedEvents.Single().ShouldBe(PlacedEvent);
    }

    [Fact]
    public void can_describe_a_projection_side_effect_publisher()
    {
        var projection = TypeDescriptor.For(typeof(OrderProjection));

        var descriptor = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            new[] { ShippedEvent },
            TargetAggregate: null)
        {
            Kind = PublisherKind.ProjectionSideEffect,
            Origin = new PublisherOrigin { ProjectionType = projection }
        };

        descriptor.Kind.ShouldBe(PublisherKind.ProjectionSideEffect);
        descriptor.Origin!.ProjectionType.ShouldBe(projection);
    }

    [Fact]
    public void with_expression_preserves_new_metadata()
    {
        // record `with` copies must carry Kind/Origin so consumers that
        // rewrite a descriptor don't silently drop the publisher classification.
        var original = new HandlerRelationshipDescriptor(
            Handler,
            Command,
            new[] { PlacedEvent },
            Aggregate)
        {
            Kind = PublisherKind.DirectBusCall,
            Origin = new PublisherOrigin { Label = "IMessageBus.PublishAsync" }
        };

        var copy = original with { TargetAggregate = null };

        copy.Kind.ShouldBe(PublisherKind.DirectBusCall);
        copy.Origin!.Label.ShouldBe("IMessageBus.PublishAsync");
        copy.TargetAggregate.ShouldBeNull();
    }

    private record Order;
    private record PlaceOrder;
    private record OrderPlaced;
    private record OrderShipped;
    private record PlaceOrderHandler;
    private record OrderProjection;
}
