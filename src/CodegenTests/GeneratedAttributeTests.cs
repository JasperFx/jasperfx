using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit;

namespace CodegenTests;

public class GeneratedAttributeTests
{
    [Fact]
    public void render_an_attribute_with_no_arguments()
    {
        var attribute = new GeneratedAttribute(typeof(SampleMarkerAttribute));

        attribute.ToCSharpDeclaration().ShouldBe("[global::CodegenTests.SampleMarker]");
        attribute.ToFSharpDeclaration().ShouldBe("[<CodegenTests.SampleMarker>]");
    }

    [Fact]
    public void the_attribute_suffix_is_trimmed_like_hand_written_code()
    {
        AttributeArg.TrimAttributeSuffix("Ns.FooAttribute").ShouldBe("Ns.Foo");
    }

    [Fact]
    public void the_attribute_suffix_is_left_alone_when_it_is_the_whole_simple_name()
    {
        // "System.Attribute" must not collapse to "System."
        AttributeArg.TrimAttributeSuffix("System.Attribute").ShouldBe("System.Attribute");
    }

    [Fact]
    public void render_a_typeof_argument()
    {
        var attribute = new GeneratedAttribute(typeof(SampleValueAttribute), AttributeArg.Type(typeof(string)));

        attribute.ToCSharpDeclaration().ShouldBe("[global::CodegenTests.SampleValue(typeof(string))]");
        attribute.ToFSharpDeclaration().ShouldBe("[<CodegenTests.SampleValue(typeof<string>)>]");
    }

    [Fact]
    public void render_a_typeof_argument_for_a_closed_generic()
    {
        AttributeArg.Type(typeof(System.Collections.Generic.List<string>)).ToCSharp()
            .ShouldBe("typeof(global::System.Collections.Generic.List<string>)");
    }

    [Fact]
    public void render_a_type_named_argument_for_a_sibling_generated_type()
    {
        // The whole point of TypeNamed: the type does not exist yet, so there is no runtime Type.
        var arg = AttributeArg.TypeNamed("MyApp.Generated.GeneratedHandlerRegistry");

        arg.ToCSharp().ShouldBe("typeof(global::MyApp.Generated.GeneratedHandlerRegistry)");
        arg.ToFSharp().ShouldBe("typeof<MyApp.Generated.GeneratedHandlerRegistry>");
    }

    [Fact]
    public void render_an_enum_argument()
    {
        var arg = AttributeArg.Enum(DynamicallyAccessedMemberTypes.All);

        arg.ToCSharp()
            .ShouldBe("global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All");
        arg.ToFSharp().ShouldBe("System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All");
    }

    [Fact]
    public void render_combined_flags_with_the_right_operator_per_language()
    {
        var arg = AttributeArg.Enum(DynamicallyAccessedMemberTypes.PublicConstructors |
                                    DynamicallyAccessedMemberTypes.PublicMethods);

        arg.ToCSharp().ShouldContain(" | ");
        arg.ToCSharp().ShouldContain("PublicConstructors");
        arg.ToCSharp().ShouldContain("PublicMethods");

        arg.ToFSharp().ShouldContain(" ||| ");
    }

    [Fact]
    public void render_literal_values()
    {
        AttributeArg.Value("hello").ToCSharp().ShouldBe("\"hello\"");
        AttributeArg.Value(true).ToCSharp().ShouldBe("true");
        AttributeArg.Value(null).ToCSharp().ShouldBe("null");
        AttributeArg.Value(5).ToCSharp().ShouldBe("5");
        AttributeArg.Value(5L).ToCSharp().ShouldBe("5L");
    }

    [Fact]
    public void escape_quotes_and_backslashes_in_string_values()
    {
        AttributeArg.Value("say \"hi\"\\").ToCSharp().ShouldBe("\"say \\\"hi\\\"\\\\\"");
    }

    [Fact]
    public void a_double_literal_carries_a_decimal_point_in_fsharp()
    {
        AttributeArg.Value(2.0).ToCSharp().ShouldBe("2d");
        AttributeArg.Value(2.0).ToFSharp().ShouldBe("2.0");
    }

    [Fact]
    public void value_routes_a_type_to_typeof()
    {
        AttributeArg.Value(typeof(string)).ToCSharp().ShouldBe("typeof(string)");
    }

    [Fact]
    public void raw_arguments_pass_through()
    {
        var arg = AttributeArg.Raw("Name = \"foo\"", "Name = \"bar\"");

        arg.ToCSharp().ShouldBe("Name = \"foo\"");
        arg.ToFSharp().ShouldBe("Name = \"bar\"");
    }

    [Fact]
    public void reject_a_non_attribute_type()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new GeneratedAttribute(typeof(string)));
    }

    [Fact]
    public void attribute_assemblies_are_reported_for_the_roslyn_path()
    {
        var attribute = new GeneratedAttribute(typeof(SampleValueAttribute), AttributeArg.Type(typeof(Xunit.FactAttribute)));

        attribute.AssemblyReferences().ShouldContain(typeof(Xunit.FactAttribute).Assembly);
    }

    [Fact]
    public void generated_types_still_carry_the_generated_code_marker_by_default()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Marked", typeof(object));
        type.AddVoidMethod("Go").Frames.Add(new CommentFrame("nothing"));

        // The [GeneratedCode] line is now modeled rather than hardcoded, but the emitted C# is
        // exactly what it always was.
        assembly.GenerateCode().ShouldContain("[global::System.CodeDom.Compiler.GeneratedCode(\"JasperFx\", \"1.0.0\")]");
    }

    [Fact]
    public void type_level_attributes_are_emitted_in_csharp()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Attributed", typeof(object));
        type.Attributes.Add(new GeneratedAttribute(typeof(SampleValueAttribute), AttributeArg.Type(typeof(string))));
        type.AddVoidMethod("Go").Frames.Add(new CommentFrame("nothing"));

        var code = assembly.GenerateCode();

        code.ShouldContain("[global::CodegenTests.SampleValue(typeof(string))]");
        code.ShouldContain("public sealed class Attributed");
    }

    [Fact]
    public void method_level_attributes_are_emitted_in_csharp()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("MethodAttributed", typeof(object));
        var method = type.AddVoidMethod("Go");
        method.Attributes.Add(new GeneratedAttribute(typeof(SampleMarkerAttribute)));
        method.Frames.Add(new CommentFrame("nothing"));

        assembly.GenerateCode().ShouldContain("[global::CodegenTests.SampleMarker]");
    }

    [Fact]
    public void type_level_attributes_are_emitted_in_fsharp()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("Attributed", typeof(object));
        type.Attributes.Add(new GeneratedAttribute(typeof(SampleValueAttribute), AttributeArg.Type(typeof(string))));
        type.AddVoidMethod("Go").Frames.Add(new CommentFrame("nothing"));

        var code = assembly.GenerateFSharpCode();

        code.ShouldContain("[<System.CodeDom.Compiler.GeneratedCode(\"JasperFx\", \"1.0.0\")>]");
        code.ShouldContain("[<CodegenTests.SampleValue(typeof<string>)>]");
        code.ShouldNotContain("global::");
    }

    [Fact]
    public void an_attributed_generated_type_still_compiles_through_roslyn()
    {
        var assembly = GeneratedAssembly.Empty();
        var type = assembly.AddType("CompiledWithAttributes", typeof(IWidgetMaker));
        type.Attributes.Add(new GeneratedAttribute(typeof(SampleValueAttribute), AttributeArg.Type(typeof(string))));

        var method = type.MethodFor(nameof(IWidgetMaker.Make));
        method.Attributes.Add(new GeneratedAttribute(typeof(SampleMarkerAttribute)));
        method.Frames.Code("return 42;");

        assembly.CompileAll();

        var maker = (IWidgetMaker)Activator.CreateInstance(type.CompiledType!)!;
        maker.Make().ShouldBe(42);

        type.CompiledType!.GetCustomAttributes(typeof(SampleValueAttribute), false).ShouldNotBeEmpty();
        type.CompiledType!.GetMethod(nameof(IWidgetMaker.Make))!
            .GetCustomAttributes(typeof(SampleMarkerAttribute), false).ShouldNotBeEmpty();
    }
}

public interface IWidgetMaker
{
    int Make();
}

[AttributeUsage(AttributeTargets.All)]
public class SampleMarkerAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.All)]
public class SampleValueAttribute : Attribute
{
    public SampleValueAttribute(Type type)
    {
        Type = type;
    }

    public Type Type { get; }
}
