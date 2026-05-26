using System.Collections.Generic;
using System.IO;
using System.Linq;
using JasperFx;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

public class ServiceRegistrationGeneratorTests
{
    private static string? RunGenerator(string source, OutputKind outputKind)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(JasperFxServiceAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ServiceLifetime).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(outputKind));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ServiceRegistrationGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().GeneratedTrees
            .Select(t => t.GetText().ToString())
            .FirstOrDefault(t => t.Contains("GeneratedServiceRegistrations"));
    }

    private static IReadOnlyList<Diagnostic> CompileWithGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Reference the full shared framework + the test's own dependency closure so the emitted
        // ServiceDescriptor/IServiceCollection code compiles against a realistic reference set.
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: trusted,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ServiceRegistrationGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        return output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    [Fact]
    public void generated_registration_code_compiles_without_errors()
    {
        // Proves the emitted services.Add(new ServiceDescriptor(...)) code is valid C#, not just the
        // expected text — covering closed generics, multiple attributes and all three lifetimes.
        var errors = CompileWithGenerator("""
            [assembly: JasperFx.JasperFxAssembly]
            namespace App;
            public interface IValidator<T> { }
            public interface IExtension { }
            public class Foo { }
            [JasperFx.JasperFxService(typeof(IValidator<>), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
            public class FooValidator : IValidator<Foo> { }
            [JasperFx.JasperFxService(typeof(IExtension), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
            [JasperFx.JasperFxService(typeof(Foo), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient)]
            public class MyExtension : IExtension { }
            """);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void singleton_registration_against_marker_interface()
    {
        var generated = RunGenerator("""
            namespace App;
            public interface IWolverineExtension { }
            [JasperFx.JasperFxService(typeof(IWolverineExtension), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
            public class MyExtension : IWolverineExtension { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldNotBeNull();
        generated.ShouldContain(
            "services.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(global::App.IWolverineExtension), typeof(global::App.MyExtension), global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton));");
    }

    [Fact]
    public void lifetime_defaults_to_singleton_when_omitted()
    {
        var generated = RunGenerator("""
            namespace App;
            public interface IMarker { }
            [JasperFx.JasperFxService(typeof(IMarker))]
            public class Thing : IMarker { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldNotBeNull();
        generated.ShouldContain("global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton");
    }

    [Fact]
    public void scoped_closed_generic_via_open_generic_attribute()
    {
        // The FluentValidation IValidator<T> shape: attribute names the OPEN generic; the generator
        // closes it from the implemented interface -> IValidator<Foo>.
        var generated = RunGenerator("""
            namespace App;
            public interface IValidator<T> { }
            public class Foo { }
            [JasperFx.JasperFxService(typeof(IValidator<>), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
            public class FooValidator : IValidator<Foo> { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldNotBeNull();
        generated.ShouldContain(
            "typeof(global::App.IValidator<global::App.Foo>), typeof(global::App.FooValidator), global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped");
    }

    [Fact]
    public void directly_closed_generic_service_type_is_used_as_is()
    {
        var generated = RunGenerator("""
            namespace App;
            public interface IValidator<T> { }
            public class Foo { }
            [JasperFx.JasperFxService(typeof(IValidator<Foo>), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient)]
            public class FooValidator : IValidator<Foo> { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldNotBeNull();
        generated.ShouldContain(
            "typeof(global::App.IValidator<global::App.Foo>), typeof(global::App.FooValidator), global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Transient");
    }

    [Fact]
    public void multiple_attributes_register_multiple_service_types()
    {
        var generated = RunGenerator("""
            namespace App;
            public interface IA { }
            public interface IB { }
            [JasperFx.JasperFxService(typeof(IA), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
            [JasperFx.JasperFxService(typeof(IB), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
            public class Both : IA, IB { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldNotBeNull();
        generated.ShouldContain("typeof(global::App.IA), typeof(global::App.Both)");
        generated.ShouldContain("typeof(global::App.IB), typeof(global::App.Both)");
    }

    [Fact]
    public void library_without_eligibility_emits_nothing()
    {
        var generated = RunGenerator("""
            namespace Lib;
            public interface IMarker { }
            [JasperFx.JasperFxService(typeof(IMarker))]
            public class Thing : IMarker { }
            """, OutputKind.DynamicallyLinkedLibrary);

        generated.ShouldBeNull();
    }

    [Fact]
    public void jasperfx_assembly_attribute_makes_a_library_eligible()
    {
        var generated = RunGenerator("""
            [assembly: JasperFx.JasperFxAssembly]
            namespace Lib;
            public interface IMarker { }
            [JasperFx.JasperFxService(typeof(IMarker))]
            public class Thing : IMarker { }
            """, OutputKind.DynamicallyLinkedLibrary);

        generated.ShouldNotBeNull();
        generated.ShouldContain("typeof(global::Lib.IMarker), typeof(global::Lib.Thing)");
    }

    [Fact]
    public void abstract_classes_are_skipped()
    {
        var generated = RunGenerator("""
            namespace App;
            public interface IMarker { }
            [JasperFx.JasperFxService(typeof(IMarker))]
            public abstract class AbstractThing : IMarker { }
            [JasperFx.JasperFxService(typeof(IMarker))]
            public class ConcreteThing : IMarker { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldNotBeNull();
        generated.ShouldContain("typeof(global::App.ConcreteThing)");
        generated.ShouldNotContain("AbstractThing");
    }

    [Fact]
    public void class_without_the_attribute_emits_nothing()
    {
        var generated = RunGenerator("""
            namespace App;
            public interface IMarker { }
            public class Plain : IMarker { }
            """, OutputKind.ConsoleApplication);

        generated.ShouldBeNull();
    }
}
