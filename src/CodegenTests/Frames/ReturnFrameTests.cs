using JasperFx.RuntimeCompiler.Scenarios;
using Shouldly;

namespace CodegenTests.Frames;

public class ReturnFrameTests
{
    [Fact]
    public void simple_use_case_no_value()
    {
        var result = CodegenScenario.ForBaseOf<ISimpleAction>(m => m.Frames.Return());

        result.LinesOfCode.ShouldContain("return;");
    }

    [Fact]
    public void return_a_variable_by_type()
    {
        var result = CodegenScenario.ForBuilds<int, int>(m => m.Frames.Return(typeof(int)));

        result.LinesOfCode.ShouldContain("return arg1;");
        result.Object.Create(5).ShouldBe(5);
    }

    [Fact]
    public void return_null()
    {
        var result = CodegenScenario.ForBuilds<string, string>(m => m.Frames.ReturnNull());

        result.LinesOfCode.ShouldContain("return null;");
        result.Object.Create("foo").ShouldBeNull();
    }

    [Fact]
    public void return_explicit_variable()
    {
        var result = CodegenScenario.ForBuilds<int, int>(m =>
        {
            var arg = m.Arguments.Single();
            m.Frames.Return(arg);
        });

        result.LinesOfCode.ShouldContain("return arg1;");
        result.Object.Create(5).ShouldBe(5);
    }
}

public interface ISimpleAction
{
    void Go();
}