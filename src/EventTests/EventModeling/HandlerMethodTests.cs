using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// <see cref="EventModelSliceDescriptor.HandlerMethod"/>: the user's handler method beside the user's
/// handler type, so a viewer can navigate to the code. Detail of <see cref="EventModelSliceDescriptor.HandlerType"/>
/// for merging.
/// </summary>
public class HandlerMethodTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static EventModelSliceDescriptor slice<THandler>(EventModelProvenance provenance, string? method) =>
        EventModelSliceDescriptor.Named("PlaceOrder") with
        {
            Provenance = provenance,
            HandlerType = T<THandler>(),
            HandlerMethod = method,
        };

    [Fact]
    public void a_handler_relationship_folds_its_method_into_the_slice()
    {
        var relationship = new HandlerRelationshipDescriptor(T<OrderHandler>(), T<PlaceOrder>(), [T<OrderPlaced>()], null)
        {
            HandlerMethod = "Handle",
        };

        var folded = relationship.ToSliceDescriptor();

        folded.HandlerType.ShouldBe(T<OrderHandler>());
        folded.HandlerMethod.ShouldBe("Handle");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void the_handler_method_round_trips(bool critterWatchWire)
    {
        var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();

        var json = JsonSerializer.Serialize(new EventModelDescriptor("Orders", [slice<OrderHandler>(EventModelProvenance.Derived, "Handle")]), options);
        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!.Slices.Single();

        json.ShouldContain(critterWatchWire ? "\"handlerMethod\":\"Handle\"" : "\"HandlerMethod\":\"Handle\"");
        back.HandlerType.ShouldBe(T<OrderHandler>());
        back.HandlerMethod.ShouldBe("Handle");

        var relationship = new HandlerRelationshipDescriptor(T<OrderHandler>(), T<PlaceOrder>(), [], null) { HandlerMethod = "Handle" };
        JsonSerializer.Deserialize<HandlerRelationshipDescriptor>(JsonSerializer.Serialize(relationship, options), options)!
            .HandlerMethod.ShouldBe("Handle");
    }

    [Fact]
    public void a_known_method_fills_in_an_unknown_one_for_the_same_handler_type()
    {
        foreach (var declaredFirst in new[] { true, false })
        {
            var declared = slice<OrderHandler>(EventModelProvenance.Declared, "Handle");
            var derived = slice<OrderHandler>(EventModelProvenance.Derived, null);

            var merged = declaredFirst ? declared.Merge(derived) : derived.Merge(declared);

            merged.HandlerMethod.ShouldBe("Handle");
            merged.Hotspots.ShouldBeEmpty();
        }
    }

    [Fact]
    public void the_method_travels_with_the_winning_handler_type()
    {
        var merged = slice<OrderHandler>(EventModelProvenance.Declared, "Consume")
            .Merge(slice<OtherOrderHandler>(EventModelProvenance.Derived, "Handle"));

        merged.HandlerType.ShouldBe(T<OtherOrderHandler>());
        merged.HandlerMethod.ShouldBe("Handle");
        // the handler type itself disagreed; the method does not add a second hotspot
        merged.Hotspots.ShouldHaveSingleItem().Role.ShouldBe(EventModelRole.HandlerType);

        // and a method is never borrowed from a different type
        slice<OrderHandler>(EventModelProvenance.Declared, "Consume")
            .Merge(slice<OtherOrderHandler>(EventModelProvenance.Derived, null))
            .HandlerMethod.ShouldBeNull();
    }

    [Fact]
    public void two_different_methods_on_the_same_handler_type_disagree()
    {
        var merged = slice<OrderHandler>(EventModelProvenance.Declared, "Consume")
            .Merge(slice<OrderHandler>(EventModelProvenance.Derived, "Handle"));

        merged.HandlerMethod.ShouldBe("Handle");

        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
        hotspot.Role.ShouldBe(EventModelRole.HandlerType);
        hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "OrderHandler.Handle"));
        hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "OrderHandler.Consume"));
    }

    [Fact]
    public void the_same_method_merges_silently()
    {
        var merged = slice<OrderHandler>(EventModelProvenance.Declared, "Handle")
            .Merge(slice<OrderHandler>(EventModelProvenance.Derived, "Handle"));

        merged.HandlerMethod.ShouldBe("Handle");
        merged.Hotspots.ShouldBeEmpty();
    }

    public record PlaceOrder;
    public record OrderPlaced;
    public class OrderHandler;
    public class OtherOrderHandler;
}
