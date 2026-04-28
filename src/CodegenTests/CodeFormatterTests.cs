using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Shouldly;

namespace CodegenTests;

public enum Numbers
{
    one,
    two
}

[Flags]
public enum Toppings
{
    None = 0,
    Cheese = 1,
    Pepperoni = 2,
    Mushrooms = 4
}

// Mimics the Npgsql.NpgsqlDbType shape: a non-[Flags] enum whose
// callers nevertheless OR member values together (NpgsqlDbType.Array
// is int.MinValue and gets bit-or'd with type tags).
public enum DirtyFlagless
{
    A = unchecked((int)0x80000000),
    B = 19
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
            .ShouldBe("CodegenTests.Numbers.one");
    }

    [Fact]
    public void write_flags_enum_combination()
    {
        // [Flags] enums whose Or'd value has a multi-name string representation
        // should produce a piped C# expression — never the comma-separated string
        // that Enum.ToString returns directly.
        CodeFormatter.Write(Toppings.Cheese | Toppings.Pepperoni)
            .ShouldBe("CodegenTests.Toppings.Cheese | CodegenTests.Toppings.Pepperoni");
    }

    [Fact]
    public void write_undefined_enum_value_uses_cast()
    {
        // Reproduces the Npgsql.NpgsqlDbType.Array | NpgsqlDbType.Text scenario.
        // Without [Flags], Enum.ToString returns the integer literal string,
        // which used to be emitted directly and produced uncompilable code such as
        // `CodegenTests.DirtyFlagless.-2147483629`. The fix emits a cast instead.
        var combined = (DirtyFlagless)((int)DirtyFlagless.A | (int)DirtyFlagless.B);
        var raw = (int)combined;
        CodeFormatter.Write(combined)
            .ShouldBe($"((CodegenTests.DirtyFlagless)({raw}))");
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