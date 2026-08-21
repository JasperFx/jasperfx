using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

// jasperfx#687: HandlerRelationshipDescriptor / PublisherKind / PublisherOrigin fold into the
// slice vocabulary. The type stays (the CritterWatch source generator still emits it);
// ToSliceDescriptor is the fold.
public class HandlerRelationshipFoldTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static HandlerRelationshipDescriptor relationship(PublisherKind kind, PublisherOrigin? origin = null)
        => new(T<OrderHandler>(), T<PlaceOrder>(), new[] { T<OrderPlaced>() }, T<Order>()) { Kind = kind, Origin = origin };

    [Fact]
    public void a_message_handler_is_a_command_slice_named_after_the_message()
    {
        var slice = relationship(PublisherKind.Handler).ToSliceDescriptor();

        slice.Name.ShouldBe(nameof(PlaceOrder));
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        slice.CommandType.ShouldBe(T<PlaceOrder>());
        slice.HandlerType.ShouldBe(T<OrderHandler>());
        slice.AggregateTypes.ShouldBe(new[] { T<Order>() });
        slice.EmittedEvents.ShouldBe(new[] { T<OrderPlaced>() });
        slice.TriggerOrigin.ShouldBeNull();
        slice.TriggerLabel.ShouldBeNull();
    }

    [Fact]
    public void a_stateless_handler_has_no_aggregate()
    {
        var slice = new HandlerRelationshipDescriptor(T<OrderHandler>(), T<PlaceOrder>(), Array.Empty<TypeDescriptor>(), null)
            .ToSliceDescriptor("custom");

        slice.Name.ShouldBe("custom");
        slice.AggregateTypes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PublisherKind.HttpEndpoint, SlicePattern.Command, TriggerKind.Http)]
    [InlineData(PublisherKind.GrpcEndpoint, SlicePattern.Command, TriggerKind.Grpc)]
    [InlineData(PublisherKind.DirectBusCall, SlicePattern.Command, TriggerKind.MessageHandler)]
    [InlineData(PublisherKind.Scheduled, SlicePattern.Automation, TriggerKind.JobScheduler)]
    [InlineData(PublisherKind.External, SlicePattern.Translation, TriggerKind.External)]
    public void publisher_kinds_fold_to_pattern_and_trigger_kind(PublisherKind kind, SlicePattern pattern, TriggerKind trigger)
    {
        var slice = relationship(kind).ToSliceDescriptor();
        slice.Pattern.ShouldBe(pattern);
        slice.TriggerKind.ShouldBe(trigger);
    }

    [Fact]
    public void a_projection_side_effect_is_an_automation_with_no_trigger_kind()
    {
        var origin = new PublisherOrigin { ProjectionType = T<OrderProjection>() };
        var slice = relationship(PublisherKind.ProjectionSideEffect, origin).ToSliceDescriptor();

        slice.Pattern.ShouldBe(SlicePattern.Automation);
        slice.TriggerKind.ShouldBeNull();
        slice.TriggerOrigin.ShouldBe(origin);
    }

    [Fact]
    public void the_origin_is_carried_whole_and_its_label_becomes_the_trigger_label()
    {
        var origin = new PublisherOrigin { HttpMethod = "POST", HttpRoute = "/orders", Label = "POST /orders" };
        var slice = relationship(PublisherKind.HttpEndpoint, origin).ToSliceDescriptor();

        slice.TriggerOrigin.ShouldBe(origin);
        slice.TriggerLabel.ShouldBe("POST /orders");
        slice.Elements.Single(e => e.Kind == EventModelElementKind.Trigger).Label.ShouldBe("POST /orders");
    }

    public class PlaceOrder { }
    public class OrderHandler { }
    public class Order { }
    public class OrderPlaced { }
    public class OrderProjection { }
}
