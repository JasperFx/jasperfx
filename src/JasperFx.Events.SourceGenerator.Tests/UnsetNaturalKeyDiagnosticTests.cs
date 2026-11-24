using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// JFXEVT018 (jasperfx#1287): an owned natural key that nothing in the project sets. Off by default, because a
/// registration can be made where the analyzer cannot see it. Turned on, it still stays silent whenever the
/// project itself might set the key in a way it cannot attribute to one aggregate.
/// </summary>
public class UnsetNaturalKeyDiagnosticTests
{
    private const string Preamble = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Test;

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
}

public record OrderNumber(string Value);
public record OrderPlaced(string Number);
";

    private const string UnsetOrder = @"
public class Order
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;
}
";

    private static Diagnostic[] Unset(string source, bool turnedOn = true)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (turnedOn)
        {
            options = options.WithSpecificDiagnosticOptions(
                new Dictionary<string, ReportDiagnostic> { ["JFXEVT018"] = ReportDiagnostic.Warn });
        }

        return Run(Compile(Preamble + source, options));
    }

    private static CSharpCompilation Compile(string source, CSharpCompilationOptions options)
    {
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            options);
    }

    private static Diagnostic[] Run(Compilation compilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out ImmutableArray<Diagnostic> diagnostics);
        return diagnostics.Where(x => x.Id == "JFXEVT018").ToArray();
    }

    [Fact]
    public void off_by_default()
    {
        Unset(UnsetOrder, turnedOn: false).ShouldBeEmpty();
    }

    [Fact]
    public void an_owned_key_nothing_sets_is_reported_when_turned_on()
    {
        var diagnostic = Unset(UnsetOrder).ShouldHaveSingleItem();

        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().ShouldContain("Natural key 'Order.Number' is never set by any event in this project");

        // It points at the attribute, as JFXEVT010-015 do
        var text = diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
        text.ShouldBe("NaturalKey");
    }

    [Fact]
    public void turned_on_from_a_global_config()
    {
        var compilation = Compile(Preamble + UnsetOrder,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSyntaxTreeOptionsProvider(new GlobalSeverity("JFXEVT018", ReportDiagnostic.Warn)));

        Run(compilation).ShouldHaveSingleItem();
    }

    [Fact]
    public void a_suppressed_global_config_leaves_it_off()
    {
        var compilation = Compile(Preamble + UnsetOrder,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSyntaxTreeOptionsProvider(new GlobalSeverity("JFXEVT018", ReportDiagnostic.Suppress)));

        Run(compilation).ShouldBeEmpty();
    }

    [Fact]
    public void a_source_on_the_aggregate_sets_it()
    {
        Unset(@"
public class Order
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;

    [NaturalKeySource]
    public void Apply(OrderPlaced e) => Number = new OrderNumber(e.Number);
}
").ShouldBeEmpty();
    }

    [Fact]
    public void a_source_the_runtime_refuses_for_another_reason_is_left_to_that_error()
    {
        // Not public: the runtime names this method and says to make it public, which is the better message
        Unset(@"
public class Order
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;

    [NaturalKeySource]
    private void Apply(OrderPlaced e) => Number = new OrderNumber(e.Number);
}
").ShouldBeEmpty();
    }

    [Fact]
    public void a_source_on_the_projection_class_sets_it()
    {
        Unset(UnsetOrder + @"
public class OrderProjection : SingleStreamProjection<Order, Guid>
{
    [NaturalKeySource]
    public static OrderNumber KeyFor(OrderPlaced e) => new OrderNumber(e.Number);
}
").ShouldBeEmpty();
    }

    [Fact]
    public void a_source_on_a_generic_projection_base_might_set_it()
    {
        Unset(UnsetOrder + @"
public abstract class KeyedProjection<TDoc> : SingleStreamProjection<TDoc, Guid> where TDoc : notnull
{
    [NaturalKeySource]
    public static OrderNumber KeyFor(OrderPlaced e) => new OrderNumber(e.Number);
}
").ShouldBeEmpty();
    }

    [Fact]
    public void a_source_on_a_derived_aggregate_sets_an_inherited_key()
    {
        Unset(@"
public abstract class KeyedBase
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;
}

public class Order : KeyedBase
{
    [NaturalKeySource]
    public void Apply(OrderPlaced e) => Number = new OrderNumber(e.Number);
}
").ShouldBeEmpty();
    }

    [Fact]
    public void a_source_on_an_unrelated_aggregate_does_not_set_it()
    {
        Unset(UnsetOrder + @"
public record InvoiceNumber(string Value);

public class Invoice
{
    public Guid Id { get; set; }

    [NaturalKey]
    public InvoiceNumber Number { get; set; } = default!;

    [NaturalKeySource]
    public static InvoiceNumber KeyFor(OrderPlaced e) => new InvoiceNumber(e.Number);
}
").ShouldHaveSingleItem().GetMessage().ShouldContain("'Order.Number'");
    }

    [Fact]
    public void natural_key_for_in_the_projection_sets_it()
    {
        Unset(UnsetOrder + @"
public class OrderProjection : SingleStreamProjection<Order, Guid>
{
    public OrderProjection()
    {
        NaturalKeyFor(x => x.SetBy<OrderPlaced>(e => new OrderNumber(e.Number)));
    }
}
").ShouldBeEmpty();
    }

    [Fact]
    public void natural_key_for_where_the_projection_is_registered_sets_it()
    {
        Unset(UnsetOrder + @"
public class OrderProjection : SingleStreamProjection<Order, Guid>;

public static class Registration
{
    public static OrderProjection Build()
    {
        var projection = new OrderProjection();
        projection.NaturalKeyFor(x => x.SetBy<OrderPlaced>(e => new OrderNumber(e.Number)));
        return projection;
    }
}
").ShouldBeEmpty();
    }

    [Fact]
    public void natural_key_for_another_aggregate_does_not_set_it()
    {
        Unset(UnsetOrder + @"
public record InvoiceNumber(string Value);

public class Invoice
{
    public Guid Id { get; set; }

    [NaturalKey]
    public InvoiceNumber Number { get; set; } = default!;
}

public class InvoiceProjection : SingleStreamProjection<Invoice, Guid>
{
    public InvoiceProjection()
    {
        NaturalKeyFor(x => x.SetBy<OrderPlaced>(e => new InvoiceNumber(e.Number)));
    }
}
").ShouldHaveSingleItem().GetMessage().ShouldContain("'Order.Number'");
    }

    [Fact]
    public void natural_key_for_in_generic_code_might_set_it()
    {
        Unset(UnsetOrder + @"
public static class Conventions
{
    public static void KeyByOrderPlaced<TDoc>(SingleStreamProjection<TDoc, Guid> projection) where TDoc : notnull
    {
        projection.NaturalKeyFor(x => x.SetBy<OrderPlaced>(e => new OrderNumber(e.Number)));
    }
}
").ShouldBeEmpty();
    }

    [Fact]
    public void a_mapping_added_straight_to_a_definition_might_set_it()
    {
        Unset(UnsetOrder + @"
public static class Conventions
{
    public static void Map(NaturalKeyDefinition definition)
    {
        definition.AddOrReplaceMapping(typeof(OrderPlaced), e => new OrderNumber(((OrderPlaced)e.Data).Number));
    }
}
").ShouldBeEmpty();
    }

    [Fact]
    public void mapped_keys_are_never_reported()
    {
        // Their source projection maintains the lookup
        Unset(@"
public class Order
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;

    [NaturalKeySource]
    public void Apply(OrderPlaced e) => Number = new OrderNumber(e.Number);
}

public class OrderSummary
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Order>]
    public OrderNumber Number { get; set; } = default!;
}
").ShouldBeEmpty();
    }

    private sealed class GlobalSeverity(string id, ReportDiagnostic severity) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
            => GeneratedKind.NotGenerated;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId,
            CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = ReportDiagnostic.Default;
            return false;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken,
            out ReportDiagnostic result)
        {
            result = diagnosticId == id ? severity : ReportDiagnostic.Default;
            return diagnosticId == id;
        }
    }
}
