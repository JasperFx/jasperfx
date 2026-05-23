using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Services;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace CodegenTests.Services;

// GH-2878 (JasperFx/wolverine): keyed services resolved through the scoped-IServiceProvider
// "service location" path lost their key — the frame always emitted GetRequiredService<T>(provider)
// instead of GetRequiredKeyedService<T>(provider, key). Service location kicks in whenever something
// in the same generated method forces it (a directly-injected IServiceProvider, or an opaque lambda
// registration like the ones MS Graph adds), which then drags every sibling dependency — including
// keyed ones — onto the service-location path where the key was being dropped.
public class keyed_service_location
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceCollection theServices = new();

    public keyed_service_location(ITestOutputHelper output)
    {
        _output = output;
    }

    // Generates a harness whose Build() calls KeyedHandler.Handle(...), exactly how Wolverine
    // generates a handler: the keyed IWidget parameter is resolved via [FromKeyedServices] during
    // code generation, and the opaque IScopedLambda sibling forces the method onto service location.
    private (string Code, ServiceContainer Graph, GeneratedType Type) generateHandlerCall()
    {
        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());

        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("KeyedHandlerHarness", typeof(ServiceHarness<WidgetResult>));
        var buildMethod = type.MethodFor("Build");

        var call = new MethodCall(typeof(KeyedHandler), nameof(KeyedHandler.Handle));
        buildMethod.Frames.Add(call);
        buildMethod.Frames.Code("return {0};", call.ReturnVariable!);

        var source = new ServiceCollectionServerVariableSource(graph);
        source.StartNewType();
        source.StartNewMethod();

        var code = assembly.GenerateCode(source);
        _output.WriteLine(code);
        return (code, graph, type);
    }

    [Fact]
    public void keyed_concrete_service_dragged_onto_location_keeps_its_key()
    {
        theServices.AddKeyedScoped<IWidget, BWidget>("blue");
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());

        var (code, _, _) = generateHandlerCall();

        code.ShouldContain("GetRequiredKeyedService");
        code.ShouldContain("\"blue\"");
        code.ShouldNotContain("GetRequiredService<CodegenTests.Services.IWidget>");
    }

    [Fact]
    public void keyed_opaque_registration_keeps_its_key()
    {
        theServices.AddKeyedScoped<IWidget>("blue", (_, _) => new BWidget());
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());

        var (code, _, _) = generateHandlerCall();

        code.ShouldContain("GetRequiredKeyedService");
        code.ShouldContain("\"blue\"");
    }

    [Fact]
    public void keyed_service_resolves_correctly_at_runtime()
    {
        // End-to-end: compile the generated harness and prove the keyed service actually resolves
        // (before the fix this threw "No service for type IWidget has been registered").
        theServices.AddKeyedScoped<IWidget, BWidget>("blue");
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());

        var (code, graph, _) = generateHandlerCall();

        var compiler = new AssemblyGenerator();
        compiler.ReferenceAssembly(GetType().Assembly);
        var builtAssembly = compiler.Generate(code);
        var builtType = builtAssembly.ExportedTypes.Single();

        var result = ((ServiceHarness<WidgetResult>)graph.BuildFromType(builtType)).Build();
        result.Widget.ShouldBeOfType<BWidget>();
    }
}

public class KeyedHandler
{
    public static WidgetResult Handle([FromKeyedServices("blue")] IWidget widget, IScopedLambda opaque)
    {
        return new WidgetResult(widget);
    }
}

public record WidgetResult(IWidget Widget);
