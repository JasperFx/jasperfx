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

public interface IFSharpAccumulator
{
    FSharpBox Accumulate();
}

public class FSharpBox
{
    public int Value { get; set; }
}

public class FSharpAccumulatorService
{
    public FSharpBox Advance(FSharpBox box)
    {
        box.Value++;
        return box;
    }
}

public class FSharpOrderCommand
{
}

public class FSharpOrder
{
    public FSharpOrder(FSharpOrderCommand command)
    {
    }
}

public class FSharpOrderResult
{
    public FSharpOrderResult(FSharpOrder order)
    {
    }
}

public interface IFSharpOrderRepository
{
    Task SaveAsync(FSharpOrder order);
}

public class FSharpOrderResultFactory
{
    public FSharpOrderResult Create(FSharpOrder order)
    {
        return new FSharpOrderResult(order);
    }
}

public interface IFSharpOrderHandler
{
    Task<FSharpOrderResult> Handle(FSharpOrderCommand command);
}

public interface IFSharpSyncTaskHandler
{
    Task HandleAsync(string name);
}

// Types for the DerivedVariable → Argument IsReferenced propagation test.
// Models the Wolverine pattern where a concrete argument (e.g. MessageContext) is
// exposed under an interface alias (e.g. IMessageContext) in DerivedVariables.
public interface IFakeBusContext { }
public class FakeBusContext : IFakeBusContext { }

public interface IFakeBusContextHandler
{
    Task HandleAsync(FakeBusContext context);
}

public interface IFSharpTupleConsumer
{
    void Consume();
}

public interface IFSharpAsyncTupleConsumer
{
    Task ConsumeAsync();
}

public class FSharpTupleService
{
    public Task SaveAsync(Red red)
    {
        return Task.CompletedTask;
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
        code.ShouldContain("member this.Greet(name: string) : string =");
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

        code.ShouldContain("member this.GreetAsync(name: string) : System.Threading.Tasks.Task<string> =");
        code.ShouldContain("task {");
        // await inside the computation expression binds with let!
        code.ShouldContain("let! result_of_CreateGreetingAsync = _service.CreateGreetingAsync(name)");
        // and the trailing expression uses `return` because we are inside the task block
        code.ShouldContain("return result_of_CreateGreetingAsync");
        code.ShouldContain("}");
    }

    [Fact]
    public void emits_a_bare_trailing_task_expression_for_return_from_last_node()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedDirectAsyncGreeter", typeof(IFSharpAsyncGreeter));
        var method = type.MethodFor(nameof(IFSharpAsyncGreeter.GreetAsync));

        var service = new InjectedField(typeof(FSharpGreetingService), "service");
        var call = new MethodCall(typeof(FSharpGreetingService), nameof(FSharpGreetingService.CreateGreetingAsync))
        {
            Target = service,
            ReturnAction = ReturnAction.Return
        };
        method.Frames.Add(call);

        var code = assembly.GenerateFSharpCode();

        code.ShouldContain("member this.GreetAsync(name: string) : System.Threading.Tasks.Task<string> =");
        // No state machine: the Task is returned directly, with no task block and no `return!`.
        code.ShouldNotContain("task {");
        code.ShouldNotContain("return!");
        code.ShouldContain("_service.CreateGreetingAsync(name)");
    }

    [Fact]
    public void open_statements_cover_return_and_dependency_namespaces()
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

        // From the Task<string> return type (not just the injected-field namespace)...
        code.ShouldContain("open System.Threading.Tasks");
        // ...and from the injected service + implemented interface.
        code.ShouldContain("open CodegenTests");
    }

    [Fact]
    public void renders_let_mutable_and_reassignment_for_return_action_assign()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedAccumulator", typeof(IFSharpAccumulator));
        var method = type.MethodFor(nameof(IFSharpAccumulator.Accumulate));

        var boxCtor = typeof(FSharpBox).GetConstructors().Single();
        var ctorFrame = new ConstructorFrame(typeof(FSharpBox), boxCtor);
        method.Frames.Add(ctorFrame);

        var service = new InjectedField(typeof(FSharpAccumulatorService), "service");
        var call = new MethodCall(typeof(FSharpAccumulatorService), nameof(FSharpAccumulatorService.Advance))
        {
            Target = service
        };
        call.Arguments[0] = ctorFrame.Variable;
        call.AssignResultTo(ctorFrame.Variable);
        method.Frames.Add(call);
        method.Frames.Add(new ReturnFrame(ctorFrame.Variable));

        var code = assembly.GenerateFSharpCode();

        var usage = ctorFrame.Variable.Usage;
        code.ShouldContain($"let mutable {usage} = CodegenTests.FSharpBox()");
        code.ShouldContain($"{usage} <- _service.Advance({usage})");
    }

    [Fact]
    public void emits_a_wolverine_shaped_async_handler_with_mixed_sync_and_async_calls()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedOrderHandler", typeof(IFSharpOrderHandler));
        var method = type.MethodFor(nameof(IFSharpOrderHandler.Handle));
        var command = method.Arguments[0];

        var orderCtor = typeof(FSharpOrder).GetConstructors().Single();
        var ctorFrame = new ConstructorFrame(typeof(FSharpOrder), orderCtor);
        ctorFrame.Parameters[0] = command;
        method.Frames.Add(ctorFrame);

        var repository = new InjectedField(typeof(IFSharpOrderRepository), "repository");
        var save = new MethodCall(typeof(IFSharpOrderRepository), nameof(IFSharpOrderRepository.SaveAsync))
        {
            Target = repository
        };
        save.Arguments[0] = ctorFrame.Variable;
        method.Frames.Add(save);

        var factory = new InjectedField(typeof(FSharpOrderResultFactory), "factory");
        var create = new MethodCall(typeof(FSharpOrderResultFactory), nameof(FSharpOrderResultFactory.Create))
        {
            Target = factory
        };
        create.Arguments[0] = ctorFrame.Variable;
        method.Frames.Add(create);

        method.Frames.Add(new ReturnFrame(create.ReturnVariable!));

        var code = assembly.GenerateFSharpCode();

        var order = ctorFrame.Variable.Usage;
        var result = create.ReturnVariable!.Usage;

        code.ShouldContain("task {");
        code.ShouldContain($"let {order} = CodegenTests.FSharpOrder(command)");
        code.ShouldContain($"do! _repository.SaveAsync({order})");                 // void async -> do!
        code.ShouldContain($"let {result} = _factory.Create({order})");            // sync call inside task
        code.ShouldContain($"return {result}");
    }

    [Fact]
    public void emits_task_completed_task_for_a_synchronous_task_returning_method()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedSyncTaskHandler", typeof(IFSharpSyncTaskHandler));
        var method = type.MethodFor(nameof(IFSharpSyncTaskHandler.HandleAsync));

        var service = new InjectedField(typeof(FSharpControlService), "service");
        method.Frames.Add(new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Record))
        {
            Target = service
        });

        var code = assembly.GenerateFSharpCode();

        // `name` is not consumed by any frame, so it is emitted with the `_` prefix to suppress FS1182.
        code.ShouldContain("member this.HandleAsync(_name: string) : System.Threading.Tasks.Task =");
        code.ShouldContain("_service.Record()");
        // No state machine for a synchronous body — just yield a completed Task.
        code.ShouldNotContain("task {");
        code.ShouldContain("System.Threading.Tasks.Task.CompletedTask");
    }

    [Fact]
    public void unimplemented_frame_throws_a_NotSupportedException_naming_itself()
    {
        var frame = new UnsupportedFrame();

        var ex = Should.Throw<NotSupportedException>(() =>
            frame.GenerateFSharpCode(null!, new FSharpSourceWriter()));

        ex.Message.ShouldContain(nameof(UnsupportedFrame));
    }

    [Fact]
    public void generates_let_binding_for_sync_tuple_return()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedTupleConsumer", typeof(IFSharpTupleConsumer));
        var method = type.MethodFor(nameof(IFSharpTupleConsumer.Consume));

        var target = new InjectedField(typeof(MethodTarget), "target");
        var call = new MethodCall(typeof(MethodTarget), nameof(MethodTarget.ReturnTuple))
        {
            Target = target
        };
        method.Frames.Add(call);

        var code = assembly.GenerateFSharpCode();

        // None of the tuple members are consumed by a subsequent frame, so all slots become `_`.
        code.ShouldContain("let struct (_, _, _) = _target.ReturnTuple()");
    }

    [Fact]
    public void generates_let_bang_binding_for_async_tuple_return()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedAsyncTupleConsumer", typeof(IFSharpAsyncTupleConsumer));
        var method = type.MethodFor(nameof(IFSharpAsyncTupleConsumer.ConsumeAsync));

        var target = new InjectedField(typeof(MethodTarget), "target");
        var tupleCall = new MethodCall(typeof(MethodTarget), nameof(MethodTarget.AsyncReturnTuple))
        {
            Target = target
        };
        method.Frames.Add(tupleCall);

        // A second frame forces AsyncMode.AsyncTask so the tuple binding uses let! inside a task block.
        var service = new InjectedField(typeof(FSharpTupleService), "service");
        var saveCall = new MethodCall(typeof(FSharpTupleService), nameof(FSharpTupleService.SaveAsync))
        {
            Target = service
        };
        saveCall.Arguments[0] = tupleCall.Creates.ElementAt(0);
        method.Frames.Add(saveCall);

        var code = assembly.GenerateFSharpCode();

        code.ShouldContain("task {");
        // Only `red` (index 0) is wired to saveCall — `blue` and `green` become `_`.
        code.ShouldContain("let! struct (red, _, _) = _target.AsyncReturnTuple()");
    }

    [Fact]
    public void argument_is_not_prefixed_with_underscore_when_derived_variable_with_same_name_is_referenced()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Some.Generated"));
        var type = assembly.AddType("GeneratedFakeContextHandler", typeof(IFakeBusContextHandler));
        var method = type.MethodFor(nameof(IFakeBusContextHandler.HandleAsync));

        // Simulate the Wolverine pattern: the concrete argument (FakeBusContext context) is
        // exposed under an interface alias in DerivedVariables, and middleware marks the alias
        // as referenced without touching the original Argument object.
        // InternalsVisibleTo lets us set `IsReferenced` directly here.
        var derivedContext = new Variable(typeof(IFakeBusContext), "context");
        derivedContext.IsReferenced = true;
        method.DerivedVariables.Add(derivedContext);

        // This frame does NOT use `context` directly — it represents middleware that only
        // needs the interface alias.
        var service = new InjectedField(typeof(FSharpControlService), "service");
        method.Frames.Add(new MethodCall(typeof(FSharpControlService), nameof(FSharpControlService.Record))
        {
            Target = service
        });

        var code = assembly.GenerateFSharpCode();

        // WriteFSharpMethod must propagate IsReferenced from the DerivedVariable to the
        // Argument with the matching Usage name.  Without the fix the argument would be
        // emitted as `_context` (FS1182 suppression prefix), breaking any frame body that
        // references `context`.
        code.ShouldContain("member this.HandleAsync(context:");
        code.ShouldNotContain("member this.HandleAsync(_context:");
    }

    public class UnsupportedFrame : SyncFrame
    {
        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            // no-op; intentionally does NOT override GenerateFSharpCode
        }
    }
}
