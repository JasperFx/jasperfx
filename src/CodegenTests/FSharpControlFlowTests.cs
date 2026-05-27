using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit.Abstractions;

namespace CodegenTests;

public interface IFSharpConditional
{
    string Describe(string input);
}

public interface IFSharpToggle
{
    void Toggle(bool flag);
}

public interface IFSharpResource
{
    void Run();
}

public class FSharpControlService
{
    public string Fallback()
    {
        return "fallback";
    }

    public string Echo(string input)
    {
        return input;
    }

    public void Record()
    {
    }

    public void Begin()
    {
    }

    public void End()
    {
    }
}

public class FSharpControlFlowTests
{
    private readonly ITestOutputHelper _output;

    public FSharpControlFlowTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void if_else_null_guard_emits_an_fsharp_if_then_else_expression()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedConditional", typeof(IFSharpConditional));
        var method = type.MethodFor(nameof(IFSharpConditional.Describe));
        var input = method.Arguments[0];

        var service = new InjectedField(typeof(FSharpControlService), "service");

        var fallback = new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Fallback))
        {
            Target = service, ReturnAction = ReturnAction.Return
        };
        var echo = new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Echo))
        {
            Target = service, ReturnAction = ReturnAction.Return
        };
        echo.Arguments[0] = input;

        method.Frames.Add(new IfElseNullGuardFrame(input, new Frame[] { fallback }, new Frame[] { echo }));

        var code = assembly.GenerateFSharpCode();
        _output.WriteLine(code);

        code.ShouldContain("if isNull input then");
        code.ShouldContain("_service.Fallback()");
        code.ShouldContain("else");
        code.ShouldContain("_service.Echo(input)");
    }

    [Fact]
    public void if_block_emits_an_fsharp_if_then()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedToggle", typeof(IFSharpToggle));
        var method = type.MethodFor(nameof(IFSharpToggle.Toggle));
        var flag = method.Arguments[0];

        var service = new InjectedField(typeof(FSharpControlService), "service");
        var record = new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Record))
        {
            Target = service
        };

        method.Frames.Add(new IfBlock(flag, record));

        var code = assembly.GenerateFSharpCode();
        _output.WriteLine(code);

        code.ShouldContain("if flag then");
        code.ShouldContain("_service.Record()");
        code.ShouldNotContain("{");
    }

    [Fact]
    public void try_finally_emits_an_fsharp_try_finally_expression()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedResource", typeof(IFSharpResource));
        var method = type.MethodFor(nameof(IFSharpResource.Run));

        var service = new InjectedField(typeof(FSharpControlService), "service");
        var begin = new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Begin))
        {
            Target = service
        };
        var work = new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Record))
        {
            Target = service
        };
        var end = new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.End))
        {
            Target = service
        };

        method.Frames.Add(new TryFinallyWrapperFrame(begin, new Frame[] { end }));
        method.Frames.Add(work);

        var code = assembly.GenerateFSharpCode();
        _output.WriteLine(code);

        code.ShouldContain("try");
        code.ShouldContain("finally");
        code.ShouldContain("_service.Begin()");
        code.ShouldContain("_service.Record()");
        code.ShouldContain("_service.End()");
    }
}
