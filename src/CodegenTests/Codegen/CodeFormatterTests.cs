using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Shouldly;
using Xunit;

namespace CodegenTests.Codegen;

public enum Numbers
{
    one,
    two
}

public class CodeFormatterTests
{
    [Fact]
    public void write_string()
    {
        CodeFormatter.Write("Hello!")
            .ShouldBe("\"Hello!\"");
    }

    [Fact]
    public void write_string_array()
    {
        CodeFormatter.Write(new string[]{"Hello!", "Bad", "Good"})
            .ShouldBe("new string[]{\"Hello!\", \"Bad\", \"Good\"}");
    }

    [Fact]
    public void write_int_array()
    {
        CodeFormatter.Write(new int[]{1, 2, 4})
            .ShouldBe("new int[]{1, 2, 4}");
        
        CodeFormatter.Write(new int[]{1, 2})
            .ShouldBe("new int[]{1, 2}");
        
        CodeFormatter.Write(new int[]{1})
            .ShouldBe("new int[]{1}");
        
        CodeFormatter.Write(new int[]{})
            .ShouldBe("new int[]{}");
    }

    [Fact]
    public void write_enum()
    {
        CodeFormatter.Write(Numbers.one)
            .ShouldBe("CodegenTests.Codegen.Numbers.one");
    }

    [Fact]
    public void write_type()
    {
        CodeFormatter.Write(GetType())
            .ShouldBe($"typeof({GetType().FullNameInCode()})");
    }

    [Fact]
    public void write_null()
    {
        CodeFormatter.Write(null).ShouldBe("null");
    }

    [Fact]
    public void write_variable()
    {
        var variable = Variable.For<int>("number");

        CodeFormatter.Write(variable).ShouldBe(variable.Usage);
    }
}