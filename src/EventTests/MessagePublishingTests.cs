using JasperFx.Events;
using Shouldly;

namespace EventTests;

// jasperfx#1045. What the dispatch is for: the batch holds an object, and the sink has to see what it
// really is. Marten calls the sink with a variable typed object, so a sink reading typeof(T) there learns
// nothing, and that is the behaviour these facts rule out.
public class MessagePublishingTests
{
    private readonly RecordingSink theSink = new();

    [Fact]
    public async Task the_tenant_overload_closes_over_the_runtime_type()
    {
        object message = new ShipOrder("order-1");

        await MessagePublishing.PublishAsync(theSink, message, "tenant-a");

        theSink.ClosedOver.ShouldBe([typeof(ShipOrder)]);
        theSink.Messages.ShouldBe([message]);
        theSink.Tenants.ShouldBe(["tenant-a"]);
    }

    [Fact]
    public async Task the_metadata_overload_closes_over_the_runtime_type_and_keeps_the_metadata()
    {
        object message = new ShipOrder("order-2");

        var metadata = new MessageMetadata("tenant-b") { CorrelationId = "c-1", CausationId = "c-2" }
            .WithHeader("region", "emea");

        await MessagePublishing.PublishAsync(theSink, message, metadata);

        theSink.ClosedOver.ShouldBe([typeof(ShipOrder)]);

        var received = theSink.Metadata.ShouldHaveSingleItem();
        received.TenantId.ShouldBe("tenant-b");
        received.CorrelationId.ShouldBe("c-1");
        received.CausationId.ShouldBe("c-2");
        received.Headers!["region"].ShouldBe("emea");
    }

    [Fact]
    public async Task each_message_type_gets_its_own_closure()
    {
        await MessagePublishing.PublishAsync(theSink, new ShipOrder("order-3"), "t");
        await MessagePublishing.PublishAsync(theSink, new CancelOrder("order-3"), "t");
        await MessagePublishing.PublishAsync(theSink, new ShipOrder("order-4"), "t");

        theSink.ClosedOver.ShouldBe([typeof(ShipOrder), typeof(CancelOrder), typeof(ShipOrder)]);
    }

    [Fact]
    public async Task a_non_public_message_type_can_be_published()
    {
        await MessagePublishing.PublishAsync(theSink, new InternalMessage(), "t");
        await MessagePublishing.PublishAsync(theSink, new PrivateMessage(), new MessageMetadata("t"));

        theSink.ClosedOver.ShouldBe([typeof(InternalMessage), typeof(PrivateMessage)]);
    }

    [Fact]
    public async Task a_value_type_message_is_unboxed_rather_than_published_as_object()
    {
        await MessagePublishing.PublishAsync(theSink, new OrderId(5), "t");

        theSink.ClosedOver.ShouldBe([typeof(OrderId)]);
        theSink.Messages.ShouldBe([new OrderId(5)]);
    }

    [Fact]
    public async Task a_closed_generic_message_type_can_be_published()
    {
        await MessagePublishing.PublishAsync(theSink, new Wrapper<ShipOrder>(new ShipOrder("order-5")), "t");

        theSink.ClosedOver.ShouldBe([typeof(Wrapper<ShipOrder>)]);
    }

    [Fact]
    public async Task an_anonymous_message_type_can_be_published()
    {
        var message = new { Name = "anonymous" };

        await MessagePublishing.PublishAsync(theSink, message, "t");

        theSink.ClosedOver.ShouldBe([message.GetType()]);
    }

    // jasperfx#1171. CompileFast emits IL through DynamicMethod and throws PlatformNotSupportedException in a
    // Native AOT image, so every projection side effect published through ProjectionBatchBase paused its
    // shard there. The interpreted branch is what such an image builds; these facts take it on a JIT runtime,
    // where RuntimeFeature.IsDynamicCodeSupported cannot be flipped, and hold it to the same contract.

    [Fact]
    public async Task the_interpreted_tenant_publisher_closes_over_the_runtime_type()
    {
        object message = new ShipOrder("order-6");

        await MessagePublishing.InterpretedTenantPublisher(message.GetType())(theSink, message, "tenant-c");

        theSink.ClosedOver.ShouldBe([typeof(ShipOrder)]);
        theSink.Messages.ShouldBe([message]);
        theSink.Tenants.ShouldBe(["tenant-c"]);
    }

    [Fact]
    public async Task the_interpreted_metadata_publisher_closes_over_the_runtime_type_and_keeps_the_metadata()
    {
        object message = new ShipOrder("order-7");
        var metadata = new MessageMetadata("tenant-d") { CorrelationId = "c-3" }.WithHeader("region", "apac");

        await MessagePublishing.InterpretedMetadataPublisher(message.GetType())(theSink, message, metadata);

        theSink.ClosedOver.ShouldBe([typeof(ShipOrder)]);

        var received = theSink.Metadata.ShouldHaveSingleItem();
        received.TenantId.ShouldBe("tenant-d");
        received.CorrelationId.ShouldBe("c-3");
        received.Headers!["region"].ShouldBe("apac");
    }

    [Fact]
    public async Task the_interpreted_publisher_handles_the_shapes_the_compiled_one_does()
    {
        await MessagePublishing.InterpretedTenantPublisher(typeof(OrderId))(theSink, new OrderId(9), "t");
        await MessagePublishing.InterpretedTenantPublisher(typeof(InternalMessage))(theSink, new InternalMessage(), "t");
        await MessagePublishing.InterpretedTenantPublisher(typeof(Wrapper<ShipOrder>))(theSink,
            new Wrapper<ShipOrder>(new ShipOrder("order-8")), "t");

        theSink.ClosedOver.ShouldBe([typeof(OrderId), typeof(InternalMessage), typeof(Wrapper<ShipOrder>)]);
        theSink.Messages[0].ShouldBe(new OrderId(9));
    }

    public record ShipOrder(string Id);

    public record CancelOrder(string Id);

    public record Wrapper<T>(T Inner);

    public readonly record struct OrderId(int Value);

    internal record InternalMessage;

    private record PrivateMessage;

    private class RecordingSink : IMessageSink
    {
        public List<Type> ClosedOver { get; } = new();
        public List<object> Messages { get; } = new();
        public List<string> Tenants { get; } = new();
        public List<MessageMetadata> Metadata { get; } = new();

        public ValueTask PublishAsync<T>(T message, string tenantId)
        {
            ClosedOver.Add(typeof(T));
            Messages.Add(message!);
            Tenants.Add(tenantId);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
        {
            ClosedOver.Add(typeof(T));
            Messages.Add(message!);
            Metadata.Add(metadata);
            return ValueTask.CompletedTask;
        }
    }
}
