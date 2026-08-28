using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Services;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace CodegenTests.Services;

// wolverine#4159. Codegen inline-constructed Lazy<T> and produced handlers that silently consumed
// nothing: `new Lazy<IFoo>()` compiles, but its factory is Activator.CreateInstance<IFoo>(), so the
// first .Value throws MissingMemberException for any T that is not publicly default-constructible --
// which is every DI-registered service. The host started healthy, listeners attached, health checks
// passed, and the handlers did nothing.
//
// The cause was in findFamily: the open-generic close-over was gated on IsNotConcrete(), and Lazy<T>
// is a concrete class. So a host's `TryAddScoped(typeof(Lazy<>), typeof(LazyResolver<>))` adapter was
// never consulted and the self-binding-any-public-concrete-type fallback won instead.
//
// Two adapters appear below, and the difference matters. LazyResolver<T> takes an IServiceProvider,
// which is what the reporter registered and what the convention looks like in the wild -- but it
// drags the whole method onto service location on its own, so it cannot tell us whether a rule
// changed anything. EagerLazyResolver<T> is inline-constructible, so the generated code visibly
// differs between "built inline from the registration" and "located from the container".
public class open_generic_registrations
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceCollection theServices = new();
    private readonly GenerationRules theRules = new();

    public open_generic_registrations(ITestOutputHelper output)
    {
        _output = output;
        theServices.AddScoped<IWidget, AWidget>();
    }

    private (string Code, ServiceContainer Graph) generate()
    {
        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());

        var assembly = new GeneratedAssembly(theRules);
        var type = assembly.AddType("LazyHarness", typeof(ServiceHarness<WidgetResult>));
        var buildMethod = type.MethodFor("Build");

        var call = new MethodCall(typeof(LazyWidgetHandler), nameof(LazyWidgetHandler.Handle));
        buildMethod.Frames.Add(call);
        buildMethod.Frames.Code("return {0};", call.ReturnVariable!);

        var source = new ServiceCollectionServerVariableSource(graph);
        source.StartNewType();
        source.StartNewMethod();

        var code = assembly.GenerateCode(source);
        _output.WriteLine(code);
        return (code, graph);
    }

    // The bug itself. `new System.Lazy<IWidget>()` is what shipped, and it is never a usable instance.
    [Fact]
    public void an_open_generic_registration_is_used_instead_of_self_binding()
    {
        theServices.TryAddScoped(typeof(Lazy<>), typeof(EagerLazyResolver<>));

        var (code, _) = generate();

        code.ShouldNotContain("new System.Lazy<");
        code.ShouldContain("new CodegenTests.Services.EagerLazyResolver<CodegenTests.Services.IWidget>");
    }

    // Same thing with the adapter shape the reporter actually registered. It needs an IServiceProvider,
    // so the method lands on service location -- either way the container's registration is what
    // produces the instance, and `new System.Lazy<>` is gone.
    [Fact]
    public void the_service_provider_flavoured_adapter_is_also_honored()
    {
        theServices.TryAddScoped(typeof(Lazy<>), typeof(LazyResolver<>));

        var (code, _) = generate();

        code.ShouldNotContain("new System.Lazy<");
        code.ShouldContain("GetRequiredService<System.Lazy<CodegenTests.Services.IWidget>>");
    }

    // The whole point: the instance the generated code hands the handler has to actually work.
    // Before the fix this threw MissingMemberException on the first .Value.
    [Theory]
    [InlineData(typeof(EagerLazyResolver<>))]
    [InlineData(typeof(LazyResolver<>))]
    public void the_resolved_lazy_can_be_dereferenced(Type adapter)
    {
        theServices.TryAddScoped(typeof(Lazy<>), adapter);

        var (code, graph) = generate();

        var compiler = new AssemblyGenerator();
        compiler.ReferenceAssembly(GetType().Assembly);
        var builtAssembly = compiler.Generate(code);
        var builtType = builtAssembly.ExportedTypes.Single();

        var result = ((ServiceHarness<WidgetResult>)graph.BuildFromType(builtType)).Build();

        result.Widget.ShouldBeOfType<AWidget>();
    }

    // No registration at all: self-binding a concrete generic is still the right answer, so nothing
    // else regresses. A Lazy<T> with no adapter registered remains the caller's problem.
    [Fact]
    public void a_concrete_generic_with_no_registration_still_self_binds()
    {
        var (code, _) = generate();

        code.ShouldContain("new System.Lazy<");
    }

    // The reporter's finding 3: the Type overload accepted an open generic and matched nothing, so the
    // call was a silent no-op. Against the inline-constructible adapter the difference is visible --
    // without the rule this method builds the resolver inline (asserted above), with it the Lazy comes
    // from the container.
    [Fact]
    public void always_use_service_location_accepts_an_open_generic()
    {
        theServices.TryAddScoped(typeof(Lazy<>), typeof(EagerLazyResolver<>));
        theRules.AlwaysUseServiceLocationFor(typeof(Lazy<>));

        var (code, _) = generate();

        code.ShouldContain("GetRequiredService<System.Lazy<CodegenTests.Services.IWidget>>");
        code.ShouldNotContain("new CodegenTests.Services.EagerLazyResolver<");
    }

    // ...and an open generic matches only its own closed forms, not every generic.
    [Fact]
    public void an_unrelated_closed_generic_is_left_alone()
    {
        theServices.TryAddScoped(typeof(Lazy<>), typeof(EagerLazyResolver<>));
        theRules.AlwaysUseServiceLocationFor(typeof(Lazy<IThingamajig>));

        var (code, _) = generate();

        code.ShouldNotContain("GetRequiredService<System.Lazy<CodegenTests.Services.IWidget>>");
        code.ShouldContain("new CodegenTests.Services.EagerLazyResolver<CodegenTests.Services.IWidget>");
    }
}

public interface IThingamajig;

/// <summary>The conventional adapter, and the one wolverine#4159 was reported against.</summary>
public class LazyResolver<T>(IServiceProvider services)
    : Lazy<T>(() => services.GetRequiredService<T>()) where T : notnull;

/// <summary>
/// Takes the service itself rather than the container, so codegen can build it inline. That makes the
/// generated code differ visibly between inline construction and service location.
/// </summary>
public class EagerLazyResolver<T>(T value) : Lazy<T>(() => value) where T : notnull;

public class LazyWidgetHandler
{
    public static WidgetResult Handle(Lazy<IWidget> widget)
    {
        return new WidgetResult(widget.Value);
    }
}
