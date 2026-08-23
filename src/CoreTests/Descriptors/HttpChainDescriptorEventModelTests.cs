using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace CoreTests.Descriptors;

// jasperfx#693 / JasperFx/wolverine#4000: HttpChainDescriptor carries the Event Modeling slice the
// route *is*, so a consumer walking endpoint by endpoint sees the slice next to the route rather
// than only through the assembled model.
//
// That this file compiles at all is half the point: CoreTests references JasperFx and nothing else,
// so an EventModelSliceDescriptor being usable here is the proof that the wire descriptors landed in
// the JasperFx assembly. Before the move they lived in JasperFx.Events, which references JasperFx --
// the wrong way round for HttpChainDescriptor to name one.
public class HttpChainDescriptorEventModelTests
{
    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static HttpChainDescriptor descriptorWithSlice() => new()
    {
        ChainId = "abc123",
        Route = "/orders/{id}/ship",
        HttpMethods = { "POST" },
        EndpointTypeFullName = "Orders.ShipOrderEndpoint",
        MethodName = "Post",
        EventModel = new EventModelSliceDescriptor(
            "ShipOrder",
            "POST /orders/{id}/ship",
            null,
            T<ShipOrder>(),
            T<ShipOrderEndpoint>(),
            new[] { T<OrderShipped>() },
            Array.Empty<TypeDescriptor>(),
            Array.Empty<TypeDescriptor>())
        {
            Pattern = SlicePattern.Command,
            TriggerKind = TriggerKind.Http,
            TriggerOrigin = new PublisherOrigin
            {
                HttpMethod = "POST",
                HttpRoute = "/orders/{id}/ship",
                Label = "POST /orders/{id}/ship"
            },
            AggregateTypes = new[] { T<Order>() },
        }
    };

    [Fact]
    public void the_slice_lives_in_the_same_assembly_as_the_descriptor_that_carries_it()
    {
        typeof(EventModelSliceDescriptor).Assembly.ShouldBeSameAs(typeof(HttpChainDescriptor).Assembly);
    }

    [Fact]
    public void the_slot_is_null_by_default()
    {
        new HttpChainDescriptor().EventModel.ShouldBeNull();
    }

    [Fact]
    public void the_slice_round_trips_on_the_critterwatch_wire()
    {
        var json = JsonSerializer.Serialize(descriptorWithSlice(), CritterWatchWire);
        var read = JsonSerializer.Deserialize<HttpChainDescriptor>(json, CritterWatchWire).ShouldNotBeNull();

        var slice = read.EventModel.ShouldNotBeNull();
        slice.Name.ShouldBe("ShipOrder");
        slice.CommandType!.Name.ShouldBe(nameof(ShipOrder));
        slice.HandlerType!.Name.ShouldBe(nameof(ShipOrderEndpoint));
        slice.EmittedEvents.Single().Name.ShouldBe(nameof(OrderShipped));
        slice.AggregateTypes.Single().Name.ShouldBe(nameof(Order));
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/orders/{id}/ship");

        // computed from the typed roles on every read, so the rendering contract survives the wire
        slice.Elements.ShouldContain(x => x.Kind == EventModelElementKind.Aggregate);
        slice.Edges.ShouldNotBeEmpty();
    }

    [Fact]
    public void the_slot_is_additive_a_payload_written_before_it_existed_still_reads()
    {
        const string olderPayload = """
            {"chainId":"abc123","route":"/orders","httpMethods":["GET"],"displayName":"Orders.Get"}
            """;

        var read = JsonSerializer.Deserialize<HttpChainDescriptor>(olderPayload, CritterWatchWire).ShouldNotBeNull();

        read.ChainId.ShouldBe("abc123");
        read.EventModel.ShouldBeNull();
    }

    public record ShipOrder(Guid Id);

    public record OrderShipped(Guid Id);

    public class ShipOrderEndpoint;

    public class Order;
}
