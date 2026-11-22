using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// JFXEVT016-017: the natural key type Wolverine's [WriteAggregate&lt;TKey&gt;] and its siblings name has to be one
/// of the model's natural keys. The attributes are stand-ins here, matched by name as in a real Wolverine app.
/// </summary>
public class ModelNaturalKeyDiagnosticTests
{
    private const string Preamble = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Wolverine.Persistence.EventSourcing
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class WriteModelAttribute : Attribute
    {
        public WriteModelAttribute() { }
        public WriteModelAttribute(string name) { }
        public Type? NaturalKey { get; set; }
    }

    public class WriteModelAttribute<TKey> : WriteModelAttribute { }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class ReadModelAttribute : Attribute
    {
        public Type? NaturalKey { get; set; }
    }

    public class ReadModelAttribute<TKey> : ReadModelAttribute { }
}

namespace Wolverine.Marten
{
    public class WriteAggregateAttribute : Wolverine.Persistence.EventSourcing.WriteModelAttribute
    {
        public WriteAggregateAttribute() { }
        public WriteAggregateAttribute(string name) { }
    }

    public class WriteAggregateAttribute<TKey> : WriteAggregateAttribute
    {
        public WriteAggregateAttribute() { }
        public WriteAggregateAttribute(string name) : base(name) { }
    }

    public class ReadAggregateAttribute : Wolverine.Persistence.EventSourcing.ReadModelAttribute { }

    public class ReadAggregateAttribute<TKey> : ReadAggregateAttribute { }
}

namespace JasperFx.Events
{
    public interface IEventStream<T> { }
}

namespace Test
{
    using Wolverine.Marten;
    using Wolverine.Persistence.EventSourcing;

    public readonly record struct Sku(string Value);
    public record ShelfCode(string Value);
    public record Unrelated(string Value);

    public class Product
    {
        public Guid Id { get; set; }

        [NaturalKey]
        public Sku Sku { get; set; }
    }

    public class Shelf
    {
        public Guid Id { get; set; }

        [NaturalKey]
        public ShelfCode Code { get; set; } = default!;

        [MappedNaturalKey<Product>]
        public Sku Sku { get; set; }
    }

    public class Untracked
    {
        public Guid Id { get; set; }
    }

    public record Command(Sku Sku, ShelfCode Code);
";

    private static Diagnostic[] ModelDiagnostics(string handlers)
    {
        var (diagnostics, _) = GeneratorHarness.Run(Preamble + handlers + "\n}\n");
        return diagnostics.Where(d => d.Id is "JFXEVT016" or "JFXEVT017").ToArray();
    }

    [Fact]
    public void owned_and_mapped_key_types_report_nothing()
    {
        ModelDiagnostics(@"
    public static class Handlers
    {
        public static void A(Command c, [WriteAggregate<ShelfCode>] Shelf shelf) { }
        public static void B(Command c, [WriteAggregate<Sku>(""Sku"")] Shelf shelf) { }
        public static void C(Command c, [ReadAggregate<Sku>] Product product) { }
        public static void D(Command c, [WriteModel<ShelfCode>] JasperFx.Events.IEventStream<Shelf> stream) { }
        public static void E(Command c, [WriteAggregate(NaturalKey = typeof(Sku))] Product product) { }
        public static void F(Command c, [WriteAggregate] Untracked untracked) { }
    }").ShouldBeEmpty();
    }

    [Fact]
    public void a_key_type_the_model_does_not_have()
    {
        var diagnostics = ModelDiagnostics(@"
    public static class Handlers
    {
        public static void A(Command c, [WriteAggregate<ShelfCode>] Product product) { }
    }");

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("JFXEVT016");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("'Test.ShelfCode'");
        diagnostic.GetMessage().ShouldContain("only has natural keys of type 'Test.Sku'");
    }

    [Fact]
    public void a_model_with_no_natural_keys()
    {
        var diagnostic = ModelDiagnostics(@"
    public static class Handlers
    {
        public static void A(Command c, [ReadModel<Sku>] Untracked untracked) { }
    }").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT016");
        diagnostic.GetMessage().ShouldContain("has no natural keys");
    }

    [Fact]
    public void the_natural_key_property_is_checked_too()
    {
        ModelDiagnostics(@"
    public static class Handlers
    {
        public static void A(Command c, [WriteModel(NaturalKey = typeof(Unrelated))] Shelf shelf) { }
    }").ShouldHaveSingleItem().Id.ShouldBe("JFXEVT016");
    }

    [Fact]
    public void a_nullable_key_type()
    {
        var diagnostic = ModelDiagnostics(@"
    public static class Handlers
    {
        public static void A(Command c, [WriteAggregate(NaturalKey = typeof(Sku?))] Product product) { }
    }").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT017");
        diagnostic.GetMessage().ShouldContain("name 'Test.Sku' instead");
    }

    [Fact]
    public void attributes_outside_wolverine_are_ignored()
    {
        ModelDiagnostics(@"
    [AttributeUsage(AttributeTargets.Parameter)]
    public class WriteAggregateAttribute<TKey> : Attribute { }

    public static class Handlers
    {
        public static void A(Command c, [Test.WriteAggregate<Unrelated>] Product product) { }
    }").ShouldBeEmpty();
    }
}
