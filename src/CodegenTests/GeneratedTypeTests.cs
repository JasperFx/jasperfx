using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Shouldly;
using Xunit.Abstractions;

namespace CodegenTests;

public class GeneratedTypeTests
{
    private readonly ITestOutputHelper _output;

    public GeneratedTypeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void write_comment()
    {
        var type = new GeneratedType("SomeClass");
        type.CommentType("some comment text");

        type.Header.ShouldBeOfType<OneLineComment>()
            .Text.ShouldBe("some comment text");
    }

    [Fact]
    public void write_multi_line_comment_header()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("SomeClass", typeof(ClassWithGenericParameter<SomeInnerClass>));
        type.MultiLineCommentType(@"
some comment text
other comment text");

        type.Header.ShouldBeOfType<MultiLineComment>();
        
        assembly.CompileAll();
        
        _output.WriteLine(type.SourceCode);

        var lines = type.SourceCode!.ReadLines().ToArray();
        lines
            .ShouldContain("    * some comment text");
        lines
            .ShouldContain("    * other comment text");
    }

    [Fact]
    public void write_comment_text_into_source_code()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("SomeClass", typeof(ClassWithGenericParameter<SomeInnerClass>));
        type.CommentType("Hey, look at this!");

        assembly.CompileAll();

        type.SourceCode.ReadLines()
            .ShouldContain("    // Hey, look at this!");
        _output.WriteLine(type.SourceCode);
    }
    
    
    [Fact]
    public void write_comment_text_into_source_code_2()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("SomeClass", typeof(ClassWithGenericParameter<SomeInnerClass>));
        type.CommentType("Hey, look at this!");

        assembly.CompileAll();

        type.SourceCode.ReadLines()
            .ShouldContain("    // Hey, look at this!");
        _output.WriteLine(type.SourceCode);
    }

    [Fact]
    public void write_footer_into_source_code()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("SomeClass", typeof(ClassWithGenericParameter<SomeInnerClass>));
        type.Footer = new OneLineComment("Hey, look at this!");

        assembly.CompileAll();

        type.SourceCode.ReadLines()
            .ShouldContain("    // Hey, look at this!");
        _output.WriteLine(type.SourceCode);
    }

    [Fact]
    public void can_replace_base_ctor_argument_with_variable()
    {
        var type = new GeneratedType("SomeClass");
        type.InheritsFrom<ClassWithCtorArgs>();

        type.AllInjectedFields.Count.ShouldBe(3);
        type.BaseConstructorArguments.Length.ShouldBe(3);

        type.UseConstantForBaseCtor(Constant.ForEnum(Color.blue));

        type.AllInjectedFields.Count.ShouldBe(2);
        type.AllInjectedFields.Any(x => x.ArgType == typeof(Color))
            .ShouldBeFalse();

        type.BaseConstructorArguments[2].ShouldBeOfType<Variable>()
            .Usage.ShouldBe("CodegenTests.Color.blue");
    }

    [Fact]
    public void generate_code_with_base_type_that_is_generic()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("SomeClass", typeof(ClassWithGenericParameter<string>));

        assembly.CompileAll();
    }

    [Fact]
    public void generate_code_with_base_type_that_is_generic_using_an_inner_type_as_the_parameter()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("SomeClass", typeof(ClassWithGenericParameter<SomeInnerClass>));

        assembly.CompileAll();

        _output.WriteLine(type.SourceCode);
    }

    [Fact]
    public void can_use_Task_as_the_class_name()
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("Task", typeof(Thing));
        var method = type.MethodFor("Do").Frames.Code("// stuff");

        assembly.CompileAll();

        _output.WriteLine(type.SourceCode);
    }

    public class SomeInnerClass
    {
    }

    [Fact]
    public void changed_props()
    {
        // This is here because of Wolverine overloads
        var rules = new GenerationRules();
        rules.TypeLoadModeHasChanged.ShouldBeFalse();
        rules.SourceCodeWritingEnabledHasChanged.ShouldBeFalse();

        // Should still mark it as changed
        rules.TypeLoadMode = TypeLoadMode.Dynamic;
        rules.TypeLoadModeHasChanged.ShouldBeTrue();
        rules.SourceCodeWritingEnabledHasChanged.ShouldBeFalse();

        rules.SourceCodeWritingEnabled = true;
        rules.TypeLoadModeHasChanged.ShouldBeTrue();
        rules.SourceCodeWritingEnabledHasChanged.ShouldBeTrue();
        
    }
}

public abstract class Thing
{
    public abstract Task Do();
}

public abstract class ClassWithGenericParameter<T>
{
    public void Go()
    {
    }
}

public abstract class ClassWithCtorArgs
{
    public ClassWithCtorArgs(string name, int age, Color color)
    {
    }

    protected abstract void Go();
}