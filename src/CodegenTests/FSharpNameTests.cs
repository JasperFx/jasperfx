using System.Collections.Generic;
using JasperFx.Core.Reflection;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Shouldly;

namespace CodegenTests;

public class FSharpNameTests
{
    // -------------------------------------------------------------------------
    // FSharpList<T> → "T list" mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void fsharp_list_maps_to_t_list_syntax()
    {
        // FSharpList<T> is the CLR name for the F# `list` type.
        // FSharpName() must produce the F# source form "T list", not the CLR name.
        typeof(FSharpList<string>).FSharpName().ShouldBe("string list");
        typeof(FSharpList<int>).FSharpName().ShouldBe("int list");
    }

    // -------------------------------------------------------------------------
    // IsFSharpRecord() detection
    // -------------------------------------------------------------------------

    [Fact]
    public void is_fsharp_record_returns_true_for_fsharp_ref_type()
    {
        // FSharpRef<T> is the CLR form of the F# record `type 'T ref = { mutable contents: 'T }`.
        // It carries CompilationMappingAttribute(SourceConstructFlags.RecordType = 2).
        typeof(FSharpRef<string>).IsFSharpRecord().ShouldBeTrue();
    }

    [Fact]
    public void is_fsharp_record_returns_false_for_csharp_class()
    {
        typeof(FSharpNameTests).IsFSharpRecord().ShouldBeFalse();
    }

    [Fact]
    public void is_fsharp_record_returns_false_for_csharp_record()
    {
        // C# positional records do not have CompilationMappingAttribute.
        typeof(SomeCSharpRecord).IsFSharpRecord().ShouldBeFalse();
    }

    public record SomeCSharpRecord(string Name, int Count);


    [Theory]
    [InlineData(typeof(int), "int")]
    [InlineData(typeof(long), "int64")]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(bool), "bool")]
    [InlineData(typeof(object), "obj")]
    [InlineData(typeof(void), "unit")]
    [InlineData(typeof(decimal), "decimal")]
    public void primitive_aliases(Type type, string expected)
    {
        type.FSharpName().ShouldBe(expected);
    }

    [Fact]
    public void double_is_float_and_single_is_float32_unlike_csharp()
    {
        typeof(double).FSharpName().ShouldBe("float");
        typeof(float).FSharpName().ShouldBe("float32");
    }

    [Fact]
    public void arrays()
    {
        typeof(int[]).FSharpName().ShouldBe("int[]");
        typeof(string[]).FSharpName().ShouldBe("string[]");
    }

    [Fact]
    public void closed_generics()
    {
        typeof(List<int>).FSharpName().ShouldBe("System.Collections.Generic.List<int>");
        typeof(Dictionary<string, int>).FSharpName()
            .ShouldBe("System.Collections.Generic.Dictionary<string, int>");
    }

    [Fact]
    public void non_primitive_falls_back_to_fully_qualified_name()
    {
        typeof(FSharpNameTests).FSharpName().ShouldBe("CodegenTests.FSharpNameTests");
    }

    [Fact]
    public void throws_on_open_generic()
    {
        Should.Throw<NotSupportedException>(() => typeof(List<>).FSharpName());
    }

    [Fact]
    public void throws_on_tuple_for_now()
    {
        Should.Throw<NotSupportedException>(() => typeof((int, string)).FSharpName());
    }
}
