using JasperFx.CodeGeneration.FSharp;
using JasperFx.Core;
using Shouldly;

namespace CodegenTests;

public class FSharpSourceWriterTests
{
    [Fact]
    public void block_indents_without_emitting_a_brace()
    {
        var writer = new FSharpSourceWriter();
        writer.Write("BLOCK:type Foo() =");
        writer.Write("let x = 0");

        var lines = writer.Code().ReadLines().ToArray();

        lines[0].ShouldBe("type Foo() =");
        // No opening brace line, just an indented body line
        lines[1].ShouldBe("    let x = 0");
    }

    [Fact]
    public void end_dedents_without_emitting_a_brace()
    {
        var writer = new FSharpSourceWriter();
        writer.Write("BLOCK:type Foo() =");
        writer.Write("let x = 0");
        writer.Write("END");
        writer.Write("let y = 0");

        var lines = writer.Code().ReadLines().ToArray();

        lines.ShouldNotContain("}");
        // back at column 0 after END
        lines.ShouldContain("let y = 0");
    }

    [Fact]
    public void finish_block_only_dedents()
    {
        var writer = new FSharpSourceWriter();
        writer.Write("BLOCK:type Foo() =");
        writer.IndentionLevel.ShouldBe(1);
        writer.FinishBlock();
        writer.IndentionLevel.ShouldBe(0);

        writer.Code().ShouldNotContain("}");
    }

    [Fact]
    public void multi_level_indention()
    {
        var writer = new FSharpSourceWriter();
        writer.Write("BLOCK:type Foo() =");
        writer.Write("BLOCK:interface IBar with");
        writer.Write("member _.M() : int = 0");

        var lines = writer.Code().ReadLines().ToArray();

        lines[2].ShouldBe("        member _.M() : int = 0");
    }

    [Fact]
    public void substitutes_backticks_for_double_quotes_like_the_csharp_writer()
    {
        var writer = new FSharpSourceWriter();
        writer.Write("let greeting = `hello`");

        writer.Code().Trim().ShouldBe("let greeting = \"hello\"");
    }

    [Fact]
    public void finish_block_at_zero_throws()
    {
        var writer = new FSharpSourceWriter();
        Should.Throw<InvalidOperationException>(() => writer.FinishBlock());
    }
}
