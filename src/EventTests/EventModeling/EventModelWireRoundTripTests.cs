using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

// jasperfx#687 acceptance: everything round-trips through the wire descriptor. CritterWatch
// serialises with System.Text.Json camelCase + JsonStringEnumConverter (camelCase), so that
// is the shape under test alongside the STJ defaults.
public class EventModelWireRoundTripTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions Defaults = new();

    public static IEnumerable<object[]> Options() =>
    [
        [CritterWatchWire],
        [Defaults],
    ];

    private static EventModelDescriptor fullModel() => new("Orders", new[]
    {
        new EventModelSliceDescriptor("PlaceOrder", "User clicks Place Order", T<PlaceOrderRequest>(), T<PlaceOrder>(), T<OrderHandler>(),
            new[] { T<OrderPlaced>() }, new[] { T<OrderProjection>() }, new[] { T<OrderSummary>() })
        {
            Pattern = SlicePattern.Command,
            TriggerKind = TriggerKind.Http,
            TriggerOrigin = new PublisherOrigin { HttpMethod = "POST", HttpRoute = "/orders", Label = "POST /orders" },
            AggregateTypes = new[] { T<Order>() },
            PublishedMessages = new[] { T<NotifyWarehouse>() },
            ExternalSystems = new[] { new ExternalSystemDescriptor("Warehouse", ExternalSystemDirection.Outbound, "rabbitmq://queue/warehouse") },
            Hotspots = new[]
            {
                HotspotDescriptor.PendingSpecification("Place Order/Rejects empty cart"),
                HotspotDescriptor.Prose("Refund policy unclear when partially shipped"),
            },
            Specifications = new[]
            {
                new SpecificationDescriptor("Place Order/Place an order", new[] { T<PlaceOrder>(), T<OrderPlaced>() }),
                new SpecificationDescriptor("Place Order/Rejects empty cart"),
            },
            Domain = "Orders",
        },
        EventModelSliceDescriptor.Named("OrderList") with
        {
            Pattern = SlicePattern.View,
            TriggerKind = TriggerKind.Human,
            TriggerLabel = "Orders screen",
            ReadModelTypes = new[] { T<OrderSummary>() },
        },
        EventModelSliceDescriptor.Named("NotifyOnPlaced") with
        {
            Pattern = SlicePattern.Automation,
            TriggerKind = TriggerKind.JobScheduler,
            HandlerType = T<OrderHandler>(),
            PublishedMessages = new[] { T<NotifyWarehouse>() },
        },
        EventModelSliceDescriptor.Named("ImportLegacy") with
        {
            Pattern = SlicePattern.Translation,
            TriggerKind = TriggerKind.External,
            ExternalSystems = new[] { new ExternalSystemDescriptor("Legacy", ExternalSystemDirection.Inbound) },
            EmittedEvents = new[] { T<OrderPlaced>() },
        },
        // jasperfx#703: a slice assembled from all three rungs, so the summary Provenance and the
        // per-role ClaimedBy map are on the wire under test too, not just the single-source shape.
        (EventModelSliceDescriptor.Named("MergedFromThreeSources") with
            {
                Provenance = EventModelProvenance.Declared, Domain = "Orders",
            })
            .Merge(EventModelSliceDescriptor.Named("MergedFromThreeSources") with
            {
                Provenance = EventModelProvenance.Derived, CommandType = T<PlaceOrder>(),
            })
            .Merge(EventModelSliceDescriptor.Named("MergedFromThreeSources") with
            {
                Provenance = EventModelProvenance.Observed, EmittedEvents = new[] { T<OrderPlaced>() },
            }),
        EventModelSliceDescriptor.Named("GrpcPlace") with { TriggerKind = TriggerKind.Grpc, Pattern = SlicePattern.Command },
        EventModelSliceDescriptor.Named("BusPlace") with { TriggerKind = TriggerKind.MessageHandler, Pattern = SlicePattern.Command },
    })
    {
        Aggregates = new[] { new AggregateDescriptor(T<Order>(), AggregateKind.WriteAggregate, new[] { T<OrderPlaced>() }) },
        Hotspots = new[] { HotspotDescriptor.Prose("Do we own the SLA clock, or does the CRM?") },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public void every_role_and_element_round_trips(JsonSerializerOptions options)
    {
        var model = fullModel();

        var json = JsonSerializer.Serialize(model, options);
        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!;

        // record equality covers every positional + init slot; Elements/Edges are computed, so
        // comparing them proves the typed roles came back whole
        back.Name.ShouldBe(model.Name);
        back.Hotspots.ShouldBe(model.Hotspots);
        // AggregateDescriptor / SpecificationDescriptor carry lists, and record equality compares
        // lists by reference — so compare those member-wise
        back.Aggregates.Count.ShouldBe(model.Aggregates.Count);
        for (var i = 0; i < model.Aggregates.Count; i++)
        {
            back.Aggregates[i].Type.ShouldBe(model.Aggregates[i].Type);
            back.Aggregates[i].Kind.ShouldBe(model.Aggregates[i].Kind);
            back.Aggregates[i].AppliedEvents.ShouldBe(model.Aggregates[i].AppliedEvents);
        }
        back.Slices.Count.ShouldBe(model.Slices.Count);
        for (var i = 0; i < model.Slices.Count; i++)
        {
            var expected = model.Slices[i];
            var actual = back.Slices[i];

            actual.Name.ShouldBe(expected.Name);
            actual.Pattern.ShouldBe(expected.Pattern);
            actual.TriggerKind.ShouldBe(expected.TriggerKind);
            actual.TriggerLabel.ShouldBe(expected.TriggerLabel);
            actual.TriggerType.ShouldBe(expected.TriggerType);
            actual.TriggerOrigin.ShouldBe(expected.TriggerOrigin);
            actual.CommandType.ShouldBe(expected.CommandType);
            actual.HandlerType.ShouldBe(expected.HandlerType);
            actual.AggregateTypes.ShouldBe(expected.AggregateTypes);
            actual.EmittedEvents.ShouldBe(expected.EmittedEvents);
            actual.PublishedMessages.ShouldBe(expected.PublishedMessages);
            actual.ProjectionTypes.ShouldBe(expected.ProjectionTypes);
            actual.ReadModelTypes.ShouldBe(expected.ReadModelTypes);
            actual.ExternalSystems.ShouldBe(expected.ExternalSystems);
            actual.Hotspots.ShouldBe(expected.Hotspots);
            actual.Specifications.Count.ShouldBe(expected.Specifications.Count);
            for (var j = 0; j < expected.Specifications.Count; j++)
            {
                actual.Specifications[j].Identity.ShouldBe(expected.Specifications[j].Identity);
                actual.Specifications[j].ResolvedTypes.ShouldBe(expected.Specifications[j].ResolvedTypes);
            }
            actual.Domain.ShouldBe(expected.Domain);
            actual.Provenance.ShouldBe(expected.Provenance);
            actual.ClaimedBy.Count.ShouldBe(expected.ClaimedBy.Count);
            foreach (var claim in expected.ClaimedBy)
            {
                actual.ClaimedBy[claim.Key].ShouldBe(claim.Value);
            }

            actual.Elements.ShouldBe(expected.Elements);
            actual.Edges.ShouldBe(expected.Edges);
        }
    }

    [Fact]
    public void the_wire_carries_the_rendering_contract_in_camel_case_with_string_enums()
    {
        var json = JsonSerializer.Serialize(fullModel(), CritterWatchWire);
        using var doc = JsonDocument.Parse(json);

        var slice = doc.RootElement.GetProperty("slices")[0];
        slice.GetProperty("pattern").GetString().ShouldBe("command");
        slice.GetProperty("triggerKind").GetString().ShouldBe("http");
        slice.GetProperty("domain").GetString().ShouldBe("Orders");
        slice.GetProperty("aggregateTypes").GetArrayLength().ShouldBe(1);
        slice.GetProperty("specifications")[0].GetProperty("identity").GetString().ShouldBe("Place Order/Place an order");
        slice.GetProperty("hotspots")[0].GetProperty("origin").GetString().ShouldBe("pendingSpecification");
        slice.GetProperty("hotspots")[0].GetProperty("specificationIdentity").GetString().ShouldBe("Place Order/Rejects empty cart");
        slice.GetProperty("externalSystems")[0].GetProperty("direction").GetString().ShouldBe("outbound");

        // the computed rendering contract is ON the wire — a viewer needs no second transform
        var elements = slice.GetProperty("elements");
        elements.GetArrayLength().ShouldBeGreaterThan(0);
        var first = elements[0];
        first.GetProperty("id").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("kind").GetString().ShouldBe("trigger");
        first.GetProperty("lane").GetString().ShouldBe("wireframe");
        slice.GetProperty("edges")[0].GetProperty("fromId").GetString().ShouldNotBeNullOrEmpty();

        // the model-level aggregate element
        doc.RootElement.GetProperty("aggregates")[0].GetProperty("kind").GetString().ShouldBe("writeAggregate");

        // ...and the model-level prose hotspot (jasperfx#690)
        var modelHotspot = doc.RootElement.GetProperty("hotspots")[0];
        modelHotspot.GetProperty("origin").GetString().ShouldBe("prose");
        modelHotspot.GetProperty("text").GetString().ShouldBe("Do we own the SLA clock, or does the CRM?");
        modelHotspot.GetProperty("specificationIdentity").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [MemberData(nameof(Options))]
    public void a_payload_from_before_687_still_deserializes(JsonSerializerOptions options)
    {
        // Exactly the 2.x positional shape and nothing else — what a 2.53 producer sends.
        var old = new EventModelDescriptor("m", new[]
        {
            new EventModelSliceDescriptor("place", null, null, T<PlaceOrder>(), T<OrderHandler>(),
                new[] { T<OrderPlaced>() }, Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>()),
        });

        var json = JsonSerializer.Serialize(old, options);
        using (var doc = JsonDocument.Parse(json))
        {
            // prove the serializer wrote the new slots too (additive on the wire)...
            doc.RootElement.GetProperty(options.PropertyNamingPolicy is null ? "Slices" : "slices")[0]
                .TryGetProperty(options.PropertyNamingPolicy is null ? "Pattern" : "pattern", out _).ShouldBeTrue();
        }

        // ...and that a payload WITHOUT them deserializes to the same defaults
        var stripped = options.PropertyNamingPolicy is null
            ? """{"Name":"m","Slices":[{"Name":"place","TriggerLabel":null,"TriggerType":null,"CommandType":{"Name":"PlaceOrder","FullName":"x.PlaceOrder","AssemblyName":"x"},"HandlerType":null,"EmittedEvents":[],"ProjectionTypes":[],"ReadModelTypes":[]}]}"""
            : """{"name":"m","slices":[{"name":"place","triggerLabel":null,"triggerType":null,"commandType":{"name":"PlaceOrder","fullName":"x.PlaceOrder","assemblyName":"x"},"handlerType":null,"emittedEvents":[],"projectionTypes":[],"readModelTypes":[]}]}""";

        var back = JsonSerializer.Deserialize<EventModelDescriptor>(stripped, options)!;
        back.Aggregates.ShouldBeEmpty();
        back.Hotspots.ShouldBeEmpty();
        var slice = back.Slices.Single();
        slice.CommandType!.FullName.ShouldBe("x.PlaceOrder");
        slice.Pattern.ShouldBeNull();
        slice.TriggerKind.ShouldBeNull();
        slice.AggregateTypes.ShouldBeEmpty();
        slice.Specifications.ShouldBeEmpty();
        slice.Hotspots.ShouldBeEmpty();
        slice.Elements.Select(e => e.Kind).ShouldBe(new[] { EventModelElementKind.Command });
    }

    [Fact]
    public void specification_feature_and_scenario_are_derived_and_not_on_the_wire()
    {
        var spec = new SpecificationDescriptor("Place Order/Place an order");
        spec.Feature.ShouldBe("Place Order");
        spec.Scenario.ShouldBe("Place an order");
        new SpecificationDescriptor("Loose").Feature.ShouldBe("Loose");
        new SpecificationDescriptor("Loose").Scenario.ShouldBeNull();

        var json = JsonSerializer.Serialize(spec, CritterWatchWire);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("feature", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("scenario", out _).ShouldBeFalse();
    }

    public class PlaceOrderRequest { }
    public class PlaceOrder { }
    public class OrderHandler { }
    public class Order { }
    public class OrderPlaced { }
    public class NotifyWarehouse { }
    public class OrderProjection { }
    public class OrderSummary { }
}
