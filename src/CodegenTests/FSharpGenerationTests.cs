using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.FSharp;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;

namespace CodegenTests;

public interface IFSharpGreeter
{
    string Greet(string name);
}

public interface IFSharpAsyncGreeter
{
    Task<string> GreetAsync(string name);
}

public class FSharpGreetingService
{
    public string CreateGreeting(string name)
    {
        return "Hello " + name;
    }

    public Task<string> CreateGreetingAsync(string name)
    {
        return Task.FromResult("Hello " + name);
    }
}

public class FSharpGenerationTests
{
    [Fact]
    public void emits_expected_fsharp_for_a_type_implementing_an_interface_with_an_injected_service()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedGreeter", typeof(IFSharpGreeter));
        var method = type.MethodFor(nameof(IFSharpGreeter.Greet));

        method.Frames.Add(new CommentFrame("a comment"));

        var service = new InjectedField(typeof(FSharpGreetingService), "service");
        var call = new MethodCall(typeof(FSharpGreetingService), nameof(FSharpGreetingService.CreateGreeting))
        {
            Target = service
        };
        method.Frames.Add(call);
        method.Frames.Add(new ReturnFrame(call.ReturnVariable!));

        var code = assembly.GenerateFSharpCode();

        code.ShouldContain("namespace Some.Generated");
        code.ShouldContain("type GeneratedGreeter(service: CodegenTests.FSharpGreetingService) =");
        code.ShouldContain("let _service = service");
        code.ShouldContain("interface CodegenTests.IFSharpGreeter with");
        code.ShouldContain("member _.Greet(name: string) : string =");
        code.ShouldContain("// a comment");
        code.ShouldContain("let result_of_CreateGreeting = _service.CreateGreeting(name)");

        // F# is expression oriented: the body ends with a bare trailing expression, no `return`,
        // and there are no C# braces or semicolons anywhere.
        code.ShouldNotContain("return ");
        code.ShouldNotContain("{");
        code.ShouldNotContain(";");
    }

    [Fact]
    public void wraps_an_async_method_body_in_a_task_computation_expression()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedAsyncGreeter", typeof(IFSharpAsyncGreeter));
        var method = type.MethodFor(nameof(IFSharpAsyncGreeter.GreetAsync));

        var service = new InjectedField(typeof(FSharpGreetingService), "service");
        var call = new MethodCall(typeof(FSharpGreetingService), nameof(FSharpGreetingService.CreateGreetingAsync))
        {
            Target = service
        };
        method.Frames.Add(call);
        method.Frames.Add(new ReturnFrame(call.ReturnVariable!));

        var code = assembly.GenerateFSharpCode();

        code.ShouldContain("member _.GreetAsync(name: string) : System.Threading.Tasks.Task<string> =");
        code.ShouldContain("task {");
        // await inside the computation expression binds with let!
        code.ShouldContain("let! result_of_CreateGreetingAsync = _service.CreateGreetingAsync(name)");
        // and the trailing expression uses `return` because we are inside the task block
        code.ShouldContain("return result_of_CreateGreetingAsync");
        code.ShouldContain("}");
    }

    [Fact]
    public void unimplemented_frame_throws_a_NotSupportedException_naming_itself()
    {
        var frame = new UnsupportedFrame();

        var ex = Should.Throw<NotSupportedException>(() =>
            frame.GenerateFSharpCode(null!, new FSharpSourceWriter()));

        ex.Message.ShouldContain(nameof(UnsupportedFrame));
    }

    public class UnsupportedFrame : SyncFrame
    {
        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            // no-op; intentionally does NOT override GenerateFSharpCode
        }
    }
}
