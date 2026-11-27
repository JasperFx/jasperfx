using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

/// <summary>
/// jasperfx#1299 — the field shapes the <c>DiscoveredEventModels</c> manifest carries for the types a
/// definition names, read off the code at compile time so the console path stays AOT-clean.
/// </summary>
public class EventModelShapesGeneratorTests
{
    private const string ManifestFile = "DiscoveredEventModels.g.cs";

    private static List<MetadataReference> References()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.ComponentModel.Annotations.dll")),
            MetadataReference.CreateFromFile(typeof(EventModelDefinition).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IJasperFxExtension).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location),
        ];
    }

    private static (string manifest, Compilation output, string[] errors) Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "ShapesTestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Parse))],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new EventModelDiscoveryGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var manifest = driver.GetRunResult().GeneratedTrees
            .First(t => Path.GetFileName(t.FilePath) == ManifestFile)
            .GetText().ToString();

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        return (manifest, output, errors);
    }

    /// <summary>Compile the generated assembly and read its manifest the way AddDiscoveredEventModels does.</summary>
    private static IReadOnlyDictionary<Type, IReadOnlyList<TypeShapeDescriptor>> LoadShapes(Compilation output)
    {
        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));

        stream.Position = 0;
        var assembly = new AssemblyLoadContext("shapes-" + Guid.NewGuid(), isCollectible: true).LoadFromStream(stream);
        var manifest = assembly.GetType("JasperFx.Generated.DiscoveredEventModels")!;
        var shapes = manifest.GetProperty("Shapes", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            as IReadOnlyDictionary<Type, IReadOnlyList<TypeShapeDescriptor>>;

        return shapes.ShouldNotBeNull();
    }

    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel.DataAnnotations;
        using JasperFx;
        using JasperFx.Events.EventModeling;

        namespace App;

        """;

    private const string Orders = Preamble + """
        public record OrderId(Guid Value);
        public record OrderLine(string Sku, int Quantity);

        public record PlaceOrder(Guid CustomerId, List<OrderLine> Lines, string? Note);

        /// <summary>An order was placed.</summary>
        /// <param name="CustomerId">Who placed it.</param>
        public record OrderPlaced(
            [property: Generated] Guid OrderId,
            [Pii] Guid CustomerId,
            decimal OrderTotal,
            OrderLine[] Lines,
            DateTimeOffset? ShippedAt);

        public class Order
        {
            [Identity] public OrderId Number { get; set; } = null!;
            public int Version { get; set; }
            public required string Status { get; set; }
            public static int Instances;
            public string Internal => "x";
        }

        public class OrderSummary
        {
            [Key] public Guid Key { get; set; }
            public decimal Total { get; set; }
            public IReadOnlyList<string> Skus { get; set; } = [];
        }

        public class OrderHandler { public string Service { get; set; } = ""; }

        public class OrdersModel : EventModelDefinition
        {
            public override void Configure(EventModelBuilder model)
            {
                model.Command<PlaceOrder>()
                    .HandledBy<OrderHandler>()
                    .StartsStream<Order>()
                    .Emits<OrderPlaced>().TaggedBy<OrderId>();

                model.View<OrderSummary>().From<OrderPlaced>();
            }
        }
        """;

    [Fact]
    public void the_manifest_carries_a_shape_per_type_a_definition_names_keyed_by_definition()
    {
        var (manifest, output, errors) = Run(Orders);

        errors.ShouldBeEmpty();
        manifest.ShouldContain("public static IReadOnlyDictionary<Type, IReadOnlyList<global::JasperFx.Events.EventModeling.TypeShapeDescriptor>> Shapes");
        manifest.ShouldContain("TypeDescriptor.For(typeof(global::App.OrderPlaced))");

        var shapes = LoadShapes(output);
        var definition = shapes.Keys.ShouldHaveSingleItem();
        definition.FullName.ShouldBe("App.OrdersModel");

        shapes[definition].Select(x => x.Type.Name).ShouldBe(["Order", "OrderPlaced", "OrderSummary", "PlaceOrder"]);
        shapes[definition].ShouldAllBe(x => x.Provenance == EventModelProvenance.Derived);
    }

    [Fact]
    public void handler_and_tag_types_get_no_shape()
    {
        var (manifest, output, _) = Run(Orders);

        manifest.ShouldNotContain("typeof(global::App.OrderHandler)");

        // OrderId is a field TYPE on Order, so it is named there, but it gets no shape of its own
        var shapes = LoadShapes(output).Values.Single();
        shapes.ShouldNotContain(x => x.Type.Name == "OrderId");
        shapes.ShouldNotContain(x => x.Type.Name == "OrderHandler");
    }

    [Fact]
    public void a_records_positional_parameters_become_fields_with_their_attributes_and_doc_comments()
    {
        var (_, output, _) = Run(Orders);
        var placed = LoadShapes(output).Values.Single().Single(x => x.Type.Name == "OrderPlaced");

        placed.Fields.Select(x => x.ToString()).ShouldBe([
            "OrderId: Guid", "CustomerId: Guid", "OrderTotal: Decimal", "Lines: OrderLine[]", "ShippedAt: DateTimeOffset?",
        ]);

        var orderId = placed.Find("OrderId")!;
        orderId.Identity.ShouldBeFalse();  // the convention is a member named Id, nothing looser
        orderId.Generated.ShouldBeTrue();  // [property: Generated]

        var customer = placed.Find("CustomerId")!;
        customer.Pii.ShouldBeTrue();       // [Pii] on the parameter
        customer.Description.ShouldBe("Who placed it.");

        placed.Find("Lines")!.Cardinality.ShouldBe(FieldCardinality.Many);
        placed.Find("Lines")!.Type.Name.ShouldBe("OrderLine");
        placed.Find("ShippedAt")!.Optional.ShouldBeTrue();
        placed.Find("OrderTotal")!.Optional.ShouldBeFalse();
    }

    [Fact]
    public void nullable_reference_types_and_collections_are_read_off_the_command()
    {
        var (_, output, _) = Run(Orders);
        var command = LoadShapes(output).Values.Single().Single(x => x.Type.Name == "PlaceOrder");

        command.Find("Note")!.Optional.ShouldBeTrue();
        command.Find("Note")!.Type.Name.ShouldBe("String");
        command.Find("Lines")!.Cardinality.ShouldBe(FieldCardinality.Many);
        command.Find("Lines")!.Type.Name.ShouldBe("OrderLine");
        command.Find("CustomerId")!.Identity.ShouldBeFalse();
    }

    [Fact]
    public void identity_key_and_required_are_honoured_and_statics_and_getter_only_computed_members_are_not_fields()
    {
        var (_, output, _) = Run(Orders);
        var shapes = LoadShapes(output).Values.Single();

        var order = shapes.Single(x => x.Type.Name == "Order");
        order.Fields.Select(x => x.Name).ShouldBe(["Number", "Version", "Status", "Internal"]);
        order.Find("Number")!.Identity.ShouldBeTrue();          // [Identity]
        order.Find("Number")!.Type.Name.ShouldBe("OrderId");     // a strong-typed id is a reference to another type
        order.Find("Status")!.Optional.ShouldBeFalse();          // required

        var summary = shapes.Single(x => x.Type.Name == "OrderSummary");
        summary.Find("Key")!.Identity.ShouldBeTrue();           // [Key]
        summary.Find("Skus")!.Cardinality.ShouldBe(FieldCardinality.Many);
        summary.Find("Skus")!.Type.Name.ShouldBe("String");
    }

    [Fact]
    public void a_definition_naming_no_types_contributes_no_shapes_and_the_manifest_still_compiles()
    {
        var (manifest, output, errors) = Run(Preamble + """
            public class EmptyModel : EventModelDefinition
            {
                public override void Configure(EventModelBuilder model)
                {
                    model.Slice("ByName").Emits("Something");
                }
            }
            """);

        errors.ShouldBeEmpty();
        manifest.ShouldContain("Shapes");
        LoadShapes(output).ShouldBeEmpty();
    }

    [Fact]
    public void a_private_nested_type_is_skipped_rather_than_breaking_the_manifest()
    {
        var (_, output, errors) = Run(Preamble + """
            public class Holder
            {
                private record Hidden(int X);
                public record Visible(int Y);

                public class Model : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model)
                    {
                        model.Command<Hidden>().Emits<Visible>();
                    }
                }
            }
            """);

        errors.ShouldBeEmpty();
        var shapes = LoadShapes(output).Values.Single();

        // The private Hidden cannot be named from the manifest, so it gets no shape; Visible does
        var visible = shapes.ShouldHaveSingleItem();
        visible.Type.Name.ShouldBe("Visible");
        visible.Find("Y")!.Type.Name.ShouldBe("Int32");
    }

    [Fact]
    public async Task shapes_are_read_by_the_registration_path()
    {
        var (_, output, _) = Run(Orders);

        using var stream = new MemoryStream();
        output.Emit(stream).Success.ShouldBeTrue();
        stream.Position = 0;
        var assembly = new AssemblyLoadContext("shapes-reg-" + Guid.NewGuid(), isCollectible: true).LoadFromStream(stream);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDiscoveredEventModels(assembly);

        var source = services.Select(x => x.ImplementationInstance).OfType<IEventModelDefinitionSource>().ShouldHaveSingleItem();
        var descriptor = (await source.TryCreateAsync(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services),
            TestContext.Current.CancellationToken))!;

        descriptor.Shapes.Select(x => x.Type.Name).ShouldBe(["Order", "OrderPlaced", "OrderSummary", "PlaceOrder"], ignoreOrder: true);
        descriptor.Shapes.ShouldAllBe(x => x.Provenance == EventModelProvenance.Derived);
    }
}
