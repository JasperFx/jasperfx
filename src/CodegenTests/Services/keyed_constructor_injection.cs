using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Services;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace CodegenTests.Services;

// wolverine#3081: keyed services injected through a handler's CONSTRUCTOR (rather than a Handle
// method parameter) were resolved by type only, dropping the [FromKeyedServices] key. The
// constructor parameter fell through to the default registration — so two keyed parameters of the
// same service type both resolved to whichever registration was last (the family default).
//
// Root cause: ConstructorFrame.FindVariables resolved each parameter via
// chain.FindVariable(parameter.ParameterType) instead of the attribute-aware
// chain.FindVariable(parameter) overload that MethodCall already used — which is why keyed
// services on a Handle METHOD parameter already worked (see keyed_service_location) but keyed
// services on the handler CONSTRUCTOR did not.
public class keyed_constructor_injection
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceCollection theServices = new();

    public keyed_constructor_injection(ITestOutputHelper output)
    {
        _output = output;
    }

    // Mirrors how Wolverine builds a handler: the handler instance is created via
    // TryCreateConstructorFrames (a ConstructorFrame), and that constructor has the
    // [FromKeyedServices] parameters.
    private (string Code, ServiceContainer Graph) generateCtorHandlerCall()
    {
        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());

        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("KeyedCtorHarness", typeof(ServiceHarness<TwoWidgetResult>));
        var buildMethod = type.MethodFor("Build");

        var call = new MethodCall(typeof(KeyedCtorHandler), nameof(KeyedCtorHandler.Handle));

        foreach (var frame in graph.TryCreateConstructorFrames(new[] { call }))
        {
            buildMethod.Frames.Add(frame);
        }

        buildMethod.Frames.Add(call);
        buildMethod.Frames.Code("return {0};", call.ReturnVariable!);

        var source = new ServiceCollectionServerVariableSource(graph);
        source.StartNewType();
        source.StartNewMethod();

        var code = assembly.GenerateCode(source);
        _output.WriteLine(code);
        return (code, graph);
    }

    [Fact]
    public void each_keyed_constructor_parameter_resolves_to_its_own_registration()
    {
        theServices.AddKeyedScoped<IWidget, AWidget>("alpha");
        theServices.AddKeyedScoped<IWidget, BWidget>("blue");

        var (code, _) = generateCtorHandlerCall();

        // Before the fix both parameters resolved to the family default (the last registration,
        // BWidget), so AWidget never appeared. Each key must now resolve to its own implementation.
        code.ShouldContain("AWidget");
        code.ShouldContain("BWidget");
    }

    [Fact]
    public void keyed_constructor_parameters_resolve_correctly_at_runtime()
    {
        // End-to-end: compile the generated harness and prove each keyed constructor parameter
        // resolves to the right implementation in the right slot.
        theServices.AddKeyedScoped<IWidget, AWidget>("alpha");
        theServices.AddKeyedScoped<IWidget, BWidget>("blue");

        var (code, graph) = generateCtorHandlerCall();

        var compiler = new AssemblyGenerator();
        compiler.ReferenceAssembly(GetType().Assembly);
        var builtAssembly = compiler.Generate(code);
        var builtType = builtAssembly.ExportedTypes.Single();

        var result = ((ServiceHarness<TwoWidgetResult>)graph.BuildFromType(builtType)).Build();
        result.First.ShouldBeOfType<AWidget>();
        result.Second.ShouldBeOfType<BWidget>();
    }
}

public class KeyedCtorHandler
{
    private readonly IWidget _first;
    private readonly IWidget _second;

    public KeyedCtorHandler(
        [FromKeyedServices("alpha")] IWidget first,
        [FromKeyedServices("blue")] IWidget second)
    {
        _first = first;
        _second = second;
    }

    public TwoWidgetResult Handle()
    {
        return new TwoWidgetResult(_first, _second);
    }
}

public record TwoWidgetResult(IWidget First, IWidget Second);
