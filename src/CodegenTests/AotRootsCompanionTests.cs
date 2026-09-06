using System;
using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Shouldly;
using Xunit;

namespace CodegenTests;

public class AotRootsCompanionTests
{
    [Fact]
    public void emits_a_module_initializer_rooted_companion()
    {
        var assembly = GeneratedAssembly.Empty();
        assembly.AddType("SomeHandler", typeof(object)).AddVoidMethod("Go").Frames.Add(new CommentFrame("nothing"));

        var companion = assembly.AddAotRoots("AotRoots", new[]
        {
            "LamarGenerated.SomeHandler",
            "MyApp.Messages.CreateOrder"
        });

        companion.ShouldNotBeNull();

        var code = assembly.GenerateCode();

        code.ShouldContain("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        code.ShouldContain(
            "[global::System.Diagnostics.CodeAnalysis.DynamicDependency(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(global::LamarGenerated.SomeHandler))]");
        code.ShouldContain(
            "[global::System.Diagnostics.CodeAnalysis.DynamicDependency(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(global::MyApp.Messages.CreateOrder))]");
        code.ShouldContain("public static void Pin()");
        code.ShouldContain("public sealed class AotRoots");
    }

    [Fact]
    public void roots_types_that_do_exist_at_codegen_time()
    {
        var assembly = GeneratedAssembly.Empty();
        assembly.AddAotRoots("AotRoots", new[] { typeof(AotRootsCompanionTests) });

        assembly.GenerateCode().ShouldContain("typeof(global::CodegenTests.AotRootsCompanionTests)");
    }

    [Fact]
    public void the_companion_is_skipped_for_fsharp()
    {
        // The F# compiler does not honor ModuleInitializerAttribute, so a companion emitted into
        // F# output would read as rooting while doing nothing at all.
        var assembly = GeneratedAssembly.Empty();
        assembly.TargetLanguage = CodegenLanguage.fsharp;

        assembly.AddAotRoots("AotRoots", new[] { "Some.Type" }).ShouldBeNull();
        assembly.GeneratedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void nothing_is_emitted_for_an_empty_root_list()
    {
        var assembly = GeneratedAssembly.Empty();

        assembly.AddAotRoots("AotRoots", Array.Empty<string>()).ShouldBeNull();
        assembly.GeneratedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void the_companion_compiles_and_the_module_initializer_actually_runs()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("AotRootsCompanion.Compiled"));
        var rooted = assembly.AddType("RootedType", typeof(IWidgetMaker));
        rooted.MethodFor(nameof(IWidgetMaker.Make)).Frames.Code("return 11;");

        assembly.AddAotRoots("AotRoots", new[] { "AotRootsCompanion.Compiled.RootedType" }).ShouldNotBeNull();

        assembly.CompileAll();

        // A module initializer runs when the assembly's module is first touched, so reaching this
        // point at all proves the emitted attributes are valid enough for Roslyn AND for the
        // runtime's module initializer rules (a bad signature is a TypeLoadException, not a
        // compile error).
        var companionType = assembly.GeneratedTypes.Single(x => x.TypeName == "AotRoots").CompiledType!;
        var pin = companionType.GetMethod(AotRootsCompanionExtensions.PinMethodName);

        pin.ShouldNotBeNull();
        pin!.IsStatic.ShouldBeTrue();
        pin.GetCustomAttributes(typeof(System.Runtime.CompilerServices.ModuleInitializerAttribute), false)
            .ShouldNotBeEmpty();
        pin.GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute), false)
            .Length.ShouldBe(1);

        ((IWidgetMaker)Activator.CreateInstance(rooted.CompiledType!)!).Make().ShouldBe(11);
    }

    [Fact]
    public void a_static_void_method_may_have_an_empty_body()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Empty");
        type.AddStaticVoidMethod("DoNothing");

        assembly.GenerateCode().ShouldContain("public static void DoNothing()");
    }

    [Fact]
    public void a_static_method_is_emitted_without_a_self_identifier_in_fsharp()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Empty");
        type.AddStaticVoidMethod("DoNothing").Attributes.Add(new GeneratedAttribute(typeof(SampleMarkerAttribute)));

        var code = assembly.GenerateFSharpCode();

        code.ShouldContain("[<CodegenTests.SampleMarker>]");
        code.ShouldContain("static member DoNothing() : unit =");
        code.ShouldNotContain("this.DoNothing");
        // F# has no empty body; the unit value stands in for one.
        code.ShouldContain("()");
    }

    [Fact]
    public void an_ordinary_method_still_refuses_an_empty_body()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Empty");
        type.AddVoidMethod("DoNothing");

        Should.Throw<ArgumentOutOfRangeException>(() => assembly.GenerateCode());
    }
}
