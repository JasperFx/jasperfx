using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Shouldly;
using Xunit;

namespace CodegenTests.Bugs;

/// <summary>
/// jasperfx#1114: with GenerationRules.ReuseCompiledAssemblies on, hosts of one application in one process compile identical
/// generated source once, not once per host. Off by default.
/// </summary>
public class Bug_1114_reuse_compiled_assemblies
{
    private static (GeneratedAssembly Assembly, GeneratedType Type) generate(string ns, string typeName, bool reuse)
    {
        var assembly = new GeneratedAssembly(new GenerationRules(ns) { ReuseCompiledAssemblies = reuse });
        var type = assembly.AddType(typeName, typeof(Bug_185_empty_frame_list.TestControllerBase));
        return (assembly, type);
    }

    [Fact]
    public void identical_source_is_compiled_once_and_every_host_gets_that_assembly()
    {
        var first = generate("Bug1114.Same", "Reused", reuse: true);
        var second = generate("Bug1114.Same", "Reused", reuse: true);

        new AssemblyGenerator().Compile(first.Assembly);
        var before = AssemblyGenerator.ReusedAssemblyCount;
        new AssemblyGenerator().Compile(second.Assembly);

        second.Type.CompiledType.ShouldNotBeNull();
        second.Type.CompiledType!.Assembly.ShouldBeSameAs(first.Type.CompiledType!.Assembly);
        AssemblyGenerator.ReusedAssemblyCount.ShouldBe(before);
    }

    [Fact]
    public void off_by_default_every_host_compiles_its_own()
    {
        new GenerationRules("Bug1114.Default").ReuseCompiledAssemblies.ShouldBeFalse();

        var first = generate("Bug1114.Off", "Fresh", reuse: false);
        var second = generate("Bug1114.Off", "Fresh", reuse: false);
        new AssemblyGenerator().Compile(first.Assembly);
        new AssemblyGenerator().Compile(second.Assembly);

        second.Type.CompiledType!.Assembly.ShouldNotBeSameAs(first.Type.CompiledType!.Assembly);
    }

    [Fact]
    public void different_source_is_never_given_another_sources_assembly()
    {
        var first = generate("Bug1114.Differs", "One", reuse: true);
        var second = generate("Bug1114.Differs", "Two", reuse: true);
        new AssemblyGenerator().Compile(first.Assembly);
        new AssemblyGenerator().Compile(second.Assembly);

        second.Type.CompiledType!.Assembly.ShouldNotBeSameAs(first.Type.CompiledType!.Assembly);
        second.Type.CompiledType.Name.ShouldBe("Two");
    }

    [Fact]
    public void the_key_covers_the_referenced_builds_not_the_source_alone()
    {
        var core = typeof(object).Assembly;
        var codegen = typeof(GeneratedAssembly).Assembly;

        AssemblyGenerator.ReuseKey("class A {}", [core, codegen]).ShouldBe(AssemblyGenerator.ReuseKey("class A {}", [codegen, core]));
        AssemblyGenerator.ReuseKey("class A {}", [core]).ShouldNotBe(AssemblyGenerator.ReuseKey("class A {}", [core, codegen]));
        AssemblyGenerator.ReuseKey("class A {}", [core]).ShouldNotBe(AssemblyGenerator.ReuseKey("class B {}", [core]));
    }
}
