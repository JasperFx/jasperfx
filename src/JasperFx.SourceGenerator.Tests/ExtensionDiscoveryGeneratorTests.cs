using System.Collections.Generic;
using System.IO;
using System.Linq;
using JasperFx;
using JasperFx.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

public class ExtensionDiscoveryGeneratorTests
{
    private static string? RunGenerator(string source, OutputKind outputKind)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IJasperFxExtension).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(outputKind));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ExtensionDiscoveryGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().GeneratedTrees
            .Select(t => t.GetText().ToString())
            .FirstOrDefault(t => t.Contains("DiscoveredExtensions"));
    }

    [Fact]
    public void marker_implementer_in_executable_assembly_is_discovered()
    {
        var manifest = RunGenerator("""
            namespace App;
            public class MyExtension : JasperFx.IJasperFxExtension { }
            """, OutputKind.ConsoleApplication);

        manifest.ShouldNotBeNull();
        manifest.ShouldContain("typeof(global::App.MyExtension)");
    }

    [Fact]
    public void library_without_jasperfx_assembly_attribute_emits_nothing()
    {
        // Not eligible: a plain library that isn't a [JasperFxAssembly] and isn't an executable.
        var manifest = RunGenerator("""
            namespace Lib;
            public class MyExtension : JasperFx.IJasperFxExtension { }
            """, OutputKind.DynamicallyLinkedLibrary);

        manifest.ShouldBeNull();
    }

    [Fact]
    public void jasperfx_assembly_attribute_makes_a_library_eligible()
    {
        var manifest = RunGenerator("""
            [assembly: JasperFx.JasperFxAssembly]
            namespace Lib;
            public class MyExtension : JasperFx.IJasperFxExtension { }
            """, OutputKind.DynamicallyLinkedLibrary);

        manifest.ShouldNotBeNull();
        manifest.ShouldContain("typeof(global::Lib.MyExtension)");
    }

    [Fact]
    public void generic_module_attribute_declared_type_is_discovered_and_deduped()
    {
        // Mirrors Wolverine's [WolverineModule<T>] shape: a generic attribute deriving from
        // JasperFxAssemblyAttribute. The declared type is also a marker implementer, so it must
        // appear exactly once.
        var manifest = RunGenerator("""
            using JasperFx;
            [assembly: Lib.Module<Lib.MyExtension>]
            namespace Lib;
            public class Module<T> : JasperFxAssemblyAttribute { }
            public class MyExtension : IJasperFxExtension { }
            """, OutputKind.DynamicallyLinkedLibrary);

        manifest.ShouldNotBeNull();
        var occurrences = manifest!.Split(["typeof(global::Lib.MyExtension)"], System.StringSplitOptions.None).Length - 1;
        occurrences.ShouldBe(1);
    }

    [Fact]
    public void abstract_marker_types_are_skipped()
    {
        var manifest = RunGenerator("""
            [assembly: JasperFx.JasperFxAssembly]
            namespace Lib;
            public abstract class AbstractExtension : JasperFx.IJasperFxExtension { }
            public class ConcreteExtension : JasperFx.IJasperFxExtension { }
            """, OutputKind.DynamicallyLinkedLibrary);

        manifest.ShouldNotBeNull();
        manifest.ShouldContain("typeof(global::Lib.ConcreteExtension)");
        manifest.ShouldNotContain("AbstractExtension");
    }
}
