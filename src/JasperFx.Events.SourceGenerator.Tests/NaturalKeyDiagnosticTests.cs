using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// JFXEVT010-015: build-time validation of owned and mapped natural keys.
/// </summary>
public class NaturalKeyDiagnosticTests
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
public record CustomerCode(string Value);

// An owner: a self-aggregating snapshot with an owned key.
public class Order
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;
}

// An owner through a separate projection class.
public class Customer
{
    public Guid Id { get; set; }

    [NaturalKey]
    public CustomerCode Code { get; set; } = default!;
}

public class CustomerProjection : SingleStreamProjection<Customer, Guid>;

// Has no natural key at all.
public class Untracked
{
    public Guid Id { get; set; }
}
";

    private static Diagnostic[] NaturalKeyDiagnostics(string source)
    {
        var (diagnostics, _) = GeneratorHarness.Run(Preamble + source);
        return diagnostics.Where(d => IsNaturalKeyId(d.Id)).ToArray();
    }

    private static bool IsNaturalKeyId(string id)
        => id is "JFXEVT010" or "JFXEVT011" or "JFXEVT012" or "JFXEVT013" or "JFXEVT014" or "JFXEVT015";

    [Fact]
    public void valid_owned_and_mapped_keys_report_nothing()
    {
        NaturalKeyDiagnostics(@"
public class OrderSummary
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Order>]
    public OrderNumber Number { get; set; } = default!;

    [NaturalKey(SourceProjection = typeof(CustomerProjection))]
    public CustomerCode Customer { get; set; } = default!;
}

public record InvoiceNumber(string Value);

public class Invoice
{
    public Guid Id { get; set; }

    [NaturalKey]
    public InvoiceNumber Number { get; set; } = default!;

    [MappedNaturalKey<Order>]
    public OrderNumber Order { get; set; } = default!;
}
").ShouldBeEmpty();
    }

    [Fact]
    public void JFXEVT010_more_than_one_owned_key()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class TwoOwners
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;

    [NaturalKey]
    public CustomerCode Code { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT010");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("'TwoOwners' marks both 'Number' and 'Code'");
    }

    [Fact]
    public void JFXEVT011_two_keys_of_one_type()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class SameType
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Own { get; set; } = default!;

    [MappedNaturalKey<Order>]
    public OrderNumber Borrowed { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT011");
        diagnostic.GetMessage().ShouldContain("'Own' and 'Borrowed' of the same type 'Test.OrderNumber'");
    }

    [Fact]
    public void JFXEVT012_mapped_to_itself()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class SelfMapped
{
    public Guid Id { get; set; }

    [MappedNaturalKey<SelfMapped>]
    public OrderNumber Number { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT012");
        diagnostic.GetMessage().ShouldContain("'SelfMapped.Number'");
    }

    [Fact]
    public void JFXEVT012_mapped_to_its_own_projection_class()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class ViaProjection
{
    public Guid Id { get; set; }

    [MappedNaturalKey<ViaProjectionProjection>]
    public OrderNumber Number { get; set; } = default!;
}

public class ViaProjectionProjection : SingleStreamProjection<ViaProjection, Guid>;
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT012");
    }

    [Fact]
    public void JFXEVT013_source_is_not_a_projection_or_keyed_aggregate()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class PointsNowhere
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Untracked>]
    public OrderNumber Number { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT013");
        diagnostic.GetMessage().ShouldContain("is neither an aggregate projection nor an aggregate with a [NaturalKey] property");
    }

    [Fact]
    public void JFXEVT013_source_projection_whose_aggregate_has_no_key()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class UntrackedProjection : SingleStreamProjection<Untracked, Guid>;

public class PointsAtUnkeyed
{
    public Guid Id { get; set; }

    [MappedNaturalKey<UntrackedProjection>]
    public OrderNumber Number { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT013");
        diagnostic.GetMessage().ShouldContain("does not own a natural key");
    }

    [Fact]
    public void JFXEVT014_chained_mapping()
    {
        var diagnostics = NaturalKeyDiagnostics(@"
public class Middle
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Order>]
    public OrderNumber Number { get; set; } = default!;
}

public class Chained
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Middle>]
    public OrderNumber Number { get; set; } = default!;
}
");

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("JFXEVT014");
        diagnostic.GetMessage().ShouldContain("'Chained.Number' is mapped to 'Middle', whose 'Number' key is itself mapped");
    }

    [Fact]
    public void JFXEVT015_key_type_mismatch()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class WrongType
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Order>]
    public CustomerCode Number { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT015");
        diagnostic.GetMessage().ShouldContain("has type 'Test.CustomerCode', but its source 'Order' owns natural key 'Number' of type 'Test.OrderNumber'");
    }

    [Fact]
    public void a_nullable_struct_key_is_the_same_type_as_the_key()
    {
        NaturalKeyDiagnostics(@"
public readonly record struct Sku(string Value);

public class Product
{
    public Guid Id { get; set; }

    [NaturalKey]
    public Sku Sku { get; set; }
}

public class ProductView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Product>]
    public Sku? Sku { get; set; }
}

public class NullableOwner
{
    public Guid Id { get; set; }

    [NaturalKey]
    public Sku? Sku { get; set; }
}

public class NullableOwnerView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<NullableOwner>]
    public Sku Sku { get; set; }
}
").ShouldBeEmpty();
    }

    [Fact]
    public void JFXEVT011_a_key_and_its_nullable_form()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public readonly record struct Sku(string Value);

public class Product
{
    public Guid Id { get; set; }

    [NaturalKey]
    public Sku Sku { get; set; }
}

public class Both
{
    public Guid Id { get; set; }

    [NaturalKey]
    public Sku Own { get; set; }

    [MappedNaturalKey<Product>]
    public Sku? Borrowed { get; set; }
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT011");
        diagnostic.GetMessage().ShouldContain("of the same type 'Test.Sku'");
    }

    [Fact]
    public void the_diagnostic_points_at_the_attribute()
    {
        var source = Preamble + @"
public class WrongType
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Order>]
    public CustomerCode Number { get; set; } = default!;
}
";
        var (diagnostics, _) = GeneratorHarness.Run(source);
        var diagnostic = diagnostics.Single(d => d.Id == "JFXEVT015");

        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)
            .ShouldBe("MappedNaturalKey<Order>");
    }

    [Fact]
    public void a_partial_type_is_reported_once()
    {
        var diagnostics = NaturalKeyDiagnostics(@"
public partial class SplitOwners
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;
}

public partial class SplitOwners
{
    [NaturalKey]
    public CustomerCode Code { get; set; } = default!;
}
");

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe("JFXEVT010");
    }

    [Fact]
    public void positional_record_properties_are_checked()
    {
        NaturalKeyDiagnostics(@"
public record PositionalOwners(Guid Id, [property: NaturalKey] OrderNumber Number, [property: NaturalKey] CustomerCode Code);
").ShouldHaveSingleItem().Id.ShouldBe("JFXEVT010");
    }

    [Fact]
    public void a_source_in_a_referenced_assembly_is_resolved()
    {
        var library = CSharpCompilation.Create(
            "OwnerLibrary",
            [CSharpSyntaxTree.ParseText(@"
using System;
using JasperFx.Events.Aggregation;

namespace Owners;

public record OrderNumber(string Value);

public class Order
{
    public Guid Id { get; set; }

    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;
}
")],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        library.Emit(stream).Success.ShouldBeTrue();

        var consumer = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(@"
using System;
using JasperFx.Events.Aggregation;

namespace Test;

public record Wrong(string Value);

public class Good
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Owners.Order>]
    public Owners.OrderNumber Number { get; set; } = default!;
}

public class Bad
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Owners.Order>]
    public Wrong Number { get; set; } = default!;
}
")],
            [.. References(), MetadataReference.CreateFromImage(stream.ToArray())],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver.RunGeneratorsAndUpdateCompilation(consumer, out _, out ImmutableArray<Diagnostic> diagnostics);

        var diagnostic = diagnostics.Where(d => IsNaturalKeyId(d.Id)).ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("JFXEVT015");
        diagnostic.GetMessage().ShouldContain("'Bad.Number'");
    }

    private const string KeyedBase = @"
// Runtime discovery reads public instance properties, inherited ones included
public class KeyedBase
{
    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;
}
";

    [Fact]
    public void a_mapped_key_whose_source_inherits_its_key_reports_nothing()
    {
        NaturalKeyDiagnostics(KeyedBase + @"
public class Shipment : KeyedBase
{
    public Guid Id { get; set; }
}

public class ShipmentView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Shipment>]
    public OrderNumber Number { get; set; } = default!;
}
").ShouldBeEmpty();
    }

    [Fact]
    public void JFXEVT010_an_inherited_owned_key_and_a_declared_one()
    {
        var diagnostic = NaturalKeyDiagnostics(KeyedBase + @"
public class Parcel : KeyedBase
{
    public Guid Id { get; set; }

    [NaturalKey]
    public CustomerCode Code { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT010");
        diagnostic.GetMessage().ShouldContain("'Parcel' marks both 'Number' and 'Code'");
    }

    [Fact]
    public void JFXEVT011_an_inherited_key_and_a_declared_key_of_its_type()
    {
        var diagnostic = NaturalKeyDiagnostics(KeyedBase + @"
public class Crate : KeyedBase
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Order>]
    public OrderNumber Other { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT011");
        diagnostic.GetMessage().ShouldContain("'Crate'");
    }

    [Fact]
    public void a_problem_on_a_base_class_is_not_reported_again_on_a_subclass()
    {
        var diagnostic = NaturalKeyDiagnostics(@"
public class TwoOwnersBase
{
    [NaturalKey]
    public OrderNumber Number { get; set; } = default!;

    [NaturalKey]
    public CustomerCode Code { get; set; } = default!;
}

public record Region(string Value);

public class RegionOwner
{
    public Guid Id { get; set; }

    [NaturalKey]
    public Region Region { get; set; } = default!;
}

public class Regional : TwoOwnersBase
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegionOwner>]
    public Region Region { get; set; } = default!;
}
").ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe("JFXEVT010");
        diagnostic.GetMessage().ShouldContain("'TwoOwnersBase'");
    }

    [Fact]
    public void an_overridden_key_counts_once()
    {
        NaturalKeyDiagnostics(@"
public class VirtualKeyBase
{
    [NaturalKey]
    public virtual OrderNumber Number { get; set; } = default!;
}

public class Overriding : VirtualKeyBase
{
    public Guid Id { get; set; }

    [NaturalKey]
    public override OrderNumber Number { get; set; } = default!;
}
").ShouldBeEmpty();
    }

    private static List<MetadataReference> References()
    {
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")),
        ];
    }
}
