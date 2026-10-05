using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.FSharp;
using Shouldly;

namespace CodegenTests;

// GH-956: the scaffold command writes source a person reads and commits, purely through
// ISourceWriter, so SourceWriter's output has to look like code someone wrote by hand.
public class SourceWriterWhitespaceTests
{
    private static string code(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    [Fact]
    public void no_blank_line_before_an_enclosing_closing_brace_or_at_the_end()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:public static class ConfirmAppointmentEndpoint");
        writer.Write("BLOCK:public static void A()");
        writer.Write("var x = 1;");
        writer.FinishBlock();
        writer.Write("BLOCK:public static void B()");
        writer.Write("var y = 2;");
        writer.FinishBlock();
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "public static class ConfirmAppointmentEndpoint",
            "{",
            "    public static void A()",
            "    {",
            "        var x = 1;",
            "    }",
            "",
            "    public static void B()",
            "    {",
            "        var y = 2;",
            "    }",
            "}"));
    }

    [Fact]
    public void no_blank_line_between_a_closing_brace_and_else()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:if (x)");
        writer.Write("a();");
        writer.FinishBlock();
        writer.WriteElse();
        writer.Write("b();");
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "if (x)",
            "{",
            "    a();",
            "}",
            "else",
            "{",
            "    b();",
            "}"));
    }

    [Fact]
    public void no_blank_line_between_a_closing_brace_and_catch_or_finally()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:try");
        writer.Write("a();");
        writer.Write("END");
        writer.Write("BLOCK:catch (Exception e)");
        writer.Write("b();");
        writer.Write("END");
        writer.Write("BLOCK:finally");
        writer.Write("c();");
        writer.Write("END");

        writer.Code().ShouldBe(code(
            "try",
            "{",
            "    a();",
            "}",
            "catch (Exception e)",
            "{",
            "    b();",
            "}",
            "finally",
            "{",
            "    c();",
            "}"));
    }

    [Fact]
    public void a_closing_brace_followed_by_a_statement_keeps_its_blank_line()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:public void Go()");
        writer.Write("BLOCK:if (x)");
        writer.Write("a();");
        writer.FinishBlock();
        writer.Write("b();");
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "public void Go()",
            "{",
            "    if (x)",
            "    {",
            "        a();",
            "    }",
            "",
            "    b();",
            "}"));
    }

    [Fact]
    public void an_identifier_starting_with_else_is_not_mistaken_for_else()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:public void Go()");
        writer.Write("BLOCK:if (x)");
        writer.Write("a();");
        writer.FinishBlock();
        writer.Write("elsewhere = 1;");
        writer.FinishBlock();

        writer.Code().ShouldContain(code("    }", "", "    elsewhere = 1;"));
    }

    [Fact]
    public void no_blank_line_directly_after_an_opening_brace_or_at_the_start()
    {
        var writer = new SourceWriter();
        writer.BlankLine();
        writer.Write("BLOCK:public void Go()");
        writer.BlankLine();
        writer.Write("var x = 0;");
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "public void Go()",
            "{",
            "    var x = 0;",
            "}"));
    }

    [Fact]
    public void consecutive_blank_lines_collapse_to_one()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:public class C");
        writer.Write("BLOCK:public void A()");
        writer.FinishBlock();
        writer.BlankLine();
        writer.Write("");
        writer.Write("public int B;");
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "public class C",
            "{",
            "    public void A()",
            "    {",
            "    }",
            "",
            "    public int B;",
            "}"));
    }

    [Fact]
    public void blank_lines_carry_no_trailing_whitespace()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:public class C");
        writer.Write("int a;");
        writer.Write();
        writer.Write("int b;");
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "public class C",
            "{",
            "    int a;",
            "",
            "    int b;",
            "}"));
    }

    [Fact]
    public void finish_block_with_extra_text()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:Action a = () =>");
        writer.Write("go();");
        writer.FinishBlock(";");

        writer.Code().ShouldBe(code(
            "Action a = () =>",
            "{",
            "    go();",
            "};"));
    }

    [Theory]
    [InlineData("ENDPOINT_COUNT = 3;")]
    [InlineData("ENDING = true;")]
    [InlineData("END_OF_FILE();")]
    public void a_line_that_only_starts_with_END_is_written_not_treated_as_a_block_close(string line)
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:public class C");
        writer.Write(line);
        writer.Write("var after = 1;");
        writer.FinishBlock();

        writer.Code().ShouldBe(code(
            "public class C",
            "{",
            $"    {line}",
            "    var after = 1;",
            "}"));
    }

    [Fact]
    public void END_with_trailing_punctuation_or_text_still_closes_the_block()
    {
        var writer = new SourceWriter();
        writer.Write("BLOCK:do");
        writer.Write("go();");
        writer.Write("END while (x);");
        writer.Write("BLOCK:var a = new Action(() =>");
        writer.Write("go();");
        writer.Write("END);");

        writer.Code().ShouldBe(code(
            "do",
            "{",
            "    go();",
            "} while (x);",
            "",
            "var a = new Action(() =>",
            "{",
            "    go();",
            "});"));
    }

    [Fact]
    public void directives_with_leading_whitespace_are_still_directives()
    {
        var writer = new SourceWriter();
        writer.Write("  BLOCK:public class C");
        writer.Write("int a;");
        writer.Write("  END");

        writer.Code().ShouldBe(code(
            "public class C",
            "{",
            "    int a;",
            "}"));
    }

    [Fact]
    public void write_turns_backticks_into_double_quotes_and_write_line_is_verbatim()
    {
        var writer = new SourceWriter();
        writer.Write("var s = `text`;");
        writer.WriteLine("// run `dotnet test`");

        writer.Code().ShouldBe(code(
            "var s = \"text\";",
            "// run `dotnet test`"));
    }

    [Fact]
    public void the_fsharp_writer_does_not_treat_an_END_prefixed_line_as_a_dedent()
    {
        var writer = new FSharpSourceWriter();
        writer.Write("BLOCK:let go () =");
        writer.Write("ENDPOINT_COUNT <- 3");
        writer.Write("  END");
        writer.Write("let after = 1");

        writer.Code().ShouldBe(code(
            "let go () =",
            "    ENDPOINT_COUNT <- 3",
            "let after = 1"));
    }
}
