using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// The [assembly: NaturalKeyAggregate] marker tells the event store to auto-register a self-aggregating
/// type as the inline owner of a natural key lookup. Only an owned key may produce it: a mapped key's
/// lookup belongs to its source projection.
/// </summary>
public class NaturalKeyAggregateMarkerTests
{
    private const string Preamble = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Test;

public record OrderNumber(string Value);
public record OrderPlaced(Guid Id, string Number);
public record OrderShipped;

public record Order(Guid Id, OrderNumber Number)
{
    [NaturalKeySource]
    public static Order Create(OrderPlaced e) => new(e.Id, new OrderNumber(e.Number));
}
";

    private const string Marker = "[assembly: global::JasperFx.Events.Aggregation.NaturalKeyAggregate(typeof(global::Test.";

    private static string Generate(string source)
    {
        var (_, generatedSources) = GeneratorHarness.Run(Preamble + source);
        return string.Join("\n", generatedSources);
    }

    [Fact]
    public void an_owned_key_emits_the_marker()
    {
        var generated = Generate(@"
public record Owned(Guid Id, [property: NaturalKey] OrderNumber Number)
{
    public static Owned Create(OrderPlaced e) => new(e.Id, new OrderNumber(e.Number));
}
");

        generated.ShouldContain(Marker + "Owned))]");
    }

    [Fact]
    public void a_generic_mapped_key_does_not_emit_the_marker()
    {
        var generated = Generate(@"
public record Summary(Guid Id, [property: MappedNaturalKey<Order>] OrderNumber Number)
{
    public static Summary Create(OrderPlaced e) => new(e.Id, new OrderNumber(e.Number));
}
");

        generated.ShouldContain("SummaryEvolver");
        generated.ShouldNotContain(Marker + "Summary))]");
    }

    [Fact]
    public void a_source_projection_key_does_not_emit_the_marker()
    {
        var generated = Generate(@"
public record Shipment(Guid Id, [property: NaturalKey(SourceProjection = typeof(Order))] OrderNumber Number)
{
    public static Shipment Create(OrderPlaced e) => new(e.Id, new OrderNumber(e.Number));
}
");

        generated.ShouldContain("ShipmentEvolver");
        generated.ShouldNotContain(Marker + "Shipment))]");
    }

    [Fact]
    public void an_owned_key_alongside_a_mapped_one_still_emits_the_marker()
    {
        var generated = Generate(@"
public record InvoiceNumber(string Value);

public record Invoice(Guid Id, [property: NaturalKey] InvoiceNumber Number, [property: MappedNaturalKey<Order>] OrderNumber Order)
{
    public static Invoice Create(OrderPlaced e) => new(e.Id, new InvoiceNumber(""I-"" + e.Number), new OrderNumber(e.Number));
}
");

        generated.ShouldContain(Marker + "Invoice))]");
    }
}
