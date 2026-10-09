using System.Collections.Generic;
using System.IO;
using System.Linq;
using JasperFx.Events.EventModeling;
using JasperFx.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

/// <summary>
/// jasperfx#993 — the per-assembly <c>JasperFx.Generated.DiscoveredEventModels</c> manifest of
/// <see cref="EventModelDefinition"/> subclasses.
/// </summary>
public class EventModelDiscoveryGeneratorTests
{
    private const string ManifestFile = "DiscoveredEventModels.g.cs";

    private static List<MetadataReference> References(bool includeEvents)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        };

        if (includeEvents)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(EventModelDefinition).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(IJasperFxExtension).Assembly.Location));
        }

        return references;
    }

    private static (string? manifest, string[] errors) Run(string source, bool includeEvents = true)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References(includeEvents),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new EventModelDiscoveryGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var manifest = driver.GetRunResult().GeneratedTrees
            .FirstOrDefault(t => Path.GetFileName(t.FilePath) == ManifestFile)
            ?.GetText().ToString();

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => (d.Location.SourceTree?.FilePath ?? "").EndsWith(ManifestFile))
            .Select(d => d.ToString())
            .ToArray();

        return (manifest, errors);
    }

    private const string Preamble = """
        using JasperFx.Events.EventModeling;

        namespace App;

        """;

    [Fact]
    public void a_public_definition_is_listed_and_its_constructors_rooted()
    {
        var (manifest, errors) = Run(Preamble + """
            public class BookingModel : EventModelDefinition
            {
                public override void Configure(EventModelBuilder model) { }
            }
            """);

        manifest.ShouldNotBeNull();
        manifest.ShouldContain("typeof(global::App.BookingModel),");
        manifest.ShouldContain(
            "[DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(global::App.BookingModel))]");
        manifest.ShouldContain("public static IReadOnlyList<Type> DefinitionTypes");
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void a_definition_with_constructor_dependencies_is_listed()
    {
        var (manifest, _) = Run(Preamble + """
            public record Settings(string Name);

            public class InjectedModel : EventModelDefinition
            {
                public InjectedModel(Settings settings) { }
                public override void Configure(EventModelBuilder model) { }
            }
            """);

        manifest.ShouldNotBeNull().ShouldContain("typeof(global::App.InjectedModel)");
    }

    [Fact]
    public void an_indirect_subclass_is_listed()
    {
        var (manifest, _) = Run(Preamble + """
            public abstract class ModelBase : EventModelDefinition;

            public class ConcreteModel : ModelBase
            {
                public override void Configure(EventModelBuilder model) { }
            }
            """);

        manifest.ShouldNotBeNull().ShouldContain("typeof(global::App.ConcreteModel)");
        manifest.ShouldNotContain("ModelBase");
    }

    [Fact]
    public void abstract_classes_are_skipped()
    {
        var (manifest, _) = Run(Preamble + """
            public abstract class AbstractModel : EventModelDefinition;
            """);

        manifest.ShouldNotBeNull().ShouldNotContain("AbstractModel");
    }

    [Fact]
    public void non_public_types_are_skipped()
    {
        var (manifest, _) = Run(Preamble + """
            internal class InternalModel : EventModelDefinition
            {
                public override void Configure(EventModelBuilder model) { }
            }

            public class Outer
            {
                private class PrivateNestedModel : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model) { }
                }
            }

            internal class InternalOuter
            {
                public class PublicInsideInternalModel : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model) { }
                }
            }
            """);

        manifest.ShouldNotBeNull();
        manifest.ShouldNotContain("InternalModel");
        manifest.ShouldNotContain("PrivateNestedModel");
        manifest.ShouldNotContain("PublicInsideInternalModel");
    }

    [Fact]
    public void a_definition_without_a_public_constructor_is_skipped()
    {
        var (manifest, _) = Run(Preamble + """
            public class HiddenConstructorModel : EventModelDefinition
            {
                private HiddenConstructorModel() { }
                public override void Configure(EventModelBuilder model) { }
            }
            """);

        manifest.ShouldNotBeNull().ShouldNotContain("HiddenConstructorModel");
    }

    [Fact]
    public void generic_classes_are_skipped()
    {
        var (manifest, _) = Run(Preamble + """
            public class GenericModel<T> : EventModelDefinition
            {
                public override void Configure(EventModelBuilder model) { }
            }

            public class GenericOuter<T>
            {
                public class InsideGenericModel : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model) { }
                }
            }
            """);

        manifest.ShouldNotBeNull();
        manifest.ShouldNotContain("GenericModel");
        manifest.ShouldNotContain("InsideGenericModel");
    }

    [Fact]
    public void definitions_nested_inside_public_types_are_included()
    {
        var (manifest, errors) = Run(Preamble + """
            public static class Chapters
            {
                public class BookingChapter : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model) { }
                }
            }
            """);

        manifest.ShouldNotBeNull().ShouldContain("typeof(global::App.Chapters.BookingChapter)");
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void an_assembly_with_no_definitions_gets_an_empty_manifest()
    {
        var (manifest, errors) = Run(Preamble + """
            public class NotADefinition { }
            """);

        manifest.ShouldNotBeNull();
        manifest.ShouldContain("internal static class DiscoveredEventModels");
        manifest.ShouldNotContain("typeof(");
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void an_assembly_that_cannot_see_event_model_definition_gets_nothing()
    {
        var (manifest, _) = Run("""
            namespace App;
            public class Anything { }
            """, includeEvents: false);

        manifest.ShouldBeNull();
    }
}
