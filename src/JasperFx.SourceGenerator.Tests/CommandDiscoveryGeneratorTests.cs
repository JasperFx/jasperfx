using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JasperFx;
using JasperFx.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

// jasperfx#1141: the DiscoveredCommands manifest gains a [ModuleInitializer] that registers each
// command with a `new T()` factory, its input's factory and an [InjectService] injector, so a Native
// AOT binary can build and run commands without Activator.CreateInstance or Type.GetProperties().
public class CommandDiscoveryGeneratorTests
{
    private const string Source = """
        using System;
        using JasperFx.CommandLine;

        namespace App;

        public class Service { }

        public class PlainInput { public string Name { get; set; } = ""; }

        public class PlainCommand : JasperFxCommand<PlainInput>
        {
            public override bool Execute(PlainInput input) => true;
        }

        public class InternalCtorInput
        {
            internal InternalCtorInput() { }
            public string Name { get; set; } = "";
        }

        public class InternalCtorInputCommand : JasperFxCommand<InternalCtorInput>
        {
            public override bool Execute(InternalCtorInput input) => true;
        }

        public class NoDefaultCtorCommand : JasperFxCommand<PlainInput>
        {
            public NoDefaultCtorCommand(string name) { }
            public override bool Execute(PlainInput input) => true;
        }

        public class InjectedCommand : JasperFxCommand<PlainInput>
        {
            [InjectService]
            public Service Service { get; set; } = null!;

            public override bool Execute(PlainInput input) => true;
        }

        public class InitOnlyInjectionCommand : JasperFxCommand<PlainInput>
        {
            [InjectService]
            public Service Service { get; init; } = null!;

            public override bool Execute(PlainInput input) => true;
        }
        """;

    private static (string manifest, IReadOnlyList<Diagnostic> errors) Run()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);

        // Every platform assembly, so the generated code is compiled exactly as a consumer would.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))
            .Append(MetadataReference.CreateFromFile(typeof(IJasperFxExtension).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create("TestAssembly", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandDiscoveryGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var manifest = driver.GetRunResult().GeneratedTrees
            .Select(t => t.GetText().ToString())
            .Single(t => t.Contains("class DiscoveredCommands"));

        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (manifest, errors);
    }

    [Fact]
    public void generated_registration_compiles()
    {
        var (_, errors) = Run();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void registers_commands_from_a_module_initializer()
    {
        var (manifest, _) = Run();

        manifest.ShouldContain("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        manifest.ShouldContain(
            "GeneratedCommandRegistry.RegisterCommand(typeof(global::App.PlainCommand), static () => new global::App.PlainCommand());");
    }

    [Fact]
    public void a_command_without_a_parameterless_constructor_is_registered_without_a_factory()
    {
        var (manifest, _) = Run();

        manifest.ShouldContain("GeneratedCommandRegistry.RegisterCommand(typeof(global::App.NoDefaultCtorCommand), null);");
    }

    [Fact]
    public void registers_input_factories_including_same_assembly_internal_constructors()
    {
        var (manifest, _) = Run();

        manifest.ShouldContain(
            "GeneratedCommandRegistry.RegisterInput(typeof(global::App.PlainInput), static () => new global::App.PlainInput());");
        manifest.ShouldContain(
            "GeneratedCommandRegistry.RegisterInput(typeof(global::App.InternalCtorInput), static () => new global::App.InternalCtorInput());");
    }

    [Fact]
    public void emits_an_injector_for_inject_service_properties()
    {
        var (manifest, _) = Run();

        manifest.ShouldContain("var target = (global::App.InjectedCommand)command;");
        manifest.ShouldContain("target.Service = (global::App.Service)(services.GetService(typeof(global::App.Service))");
    }

    [Fact]
    public void leaves_a_command_it_cannot_inject_to_the_reflective_path()
    {
        var (manifest, _) = Run();

        // An init-only setter cannot be assigned from the generated delegate, so no injector.
        manifest.ShouldContain(
            "GeneratedCommandRegistry.RegisterCommand(typeof(global::App.InitOnlyInjectionCommand), static () => new global::App.InitOnlyInjectionCommand());");
        manifest.ShouldNotContain("(global::App.InitOnlyInjectionCommand)command");
    }
}
