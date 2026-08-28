using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodegenTests.Services;

// wolverine#4171. A host that wants to seed the service-location child scope with instances the
// generated method already owns used to do it from a frame in the method: find the scoped
// IServiceProvider during arrangement, cast its Creator to IScopedContainerCreation, and register
// postprocessors on it.
//
// That only ever worked when something in the method asked for an IServiceProvider *by name*. Frames
// resolve their variables in MethodFrameArranger's first pass, but the scope for an opaque
// scoped/transient registration is not created until ServiceCollectionServerVariableSource
// .ReplaceVariables runs *after* that pass -- so the activator frame found nothing, attached nothing,
// and said nothing. Wolverine shipped that way from GH-3001 until wolverine#4171: a handler whose only
// reason to service-locate was an opaque lambda got an unprimed scope, and every test covering the
// feature happened to put an IServiceProvider on the handler signature.
//
// ScopePostProcessorSources moves the attachment to the one place that knows a scope was created.
public class scope_postprocessor_sources
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceCollection theServices = new();
    private readonly List<Func<SyncFrame>> thePostProcessors = new();

    public scope_postprocessor_sources(ITestOutputHelper output)
    {
        _output = output;
    }

    private string generate<T>(string methodName)
    {
        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());

        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("ScopePrimingHarness", typeof(ServiceHarness<WidgetResult>));
        var buildMethod = type.MethodFor("Build");

        var call = new MethodCall(typeof(T), methodName);
        buildMethod.Frames.Add(call);
        buildMethod.Frames.Code("return {0};", call.ReturnVariable!);

        var source = new ServiceCollectionServerVariableSource(graph);
        source.ScopePostProcessorSources.AddRange(thePostProcessors);
        source.StartNewType();
        source.StartNewMethod();

        var code = assembly.GenerateCode(source);
        _output.WriteLine(code);
        return code;
    }

    // The case that was silently broken: nothing asks for an IServiceProvider, but the opaque scoped
    // lambda still drags the method onto service location and a scope IS created.
    [Fact]
    public void postprocessors_run_when_only_an_opaque_registration_forces_the_scope()
    {
        theServices.AddScoped<IWidget, AWidget>();
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());
        thePostProcessors.Add(() => new LinePostprocessor("// PRIMED"));

        var code = generate<OpaqueOnlyHandler>(nameof(OpaqueOnlyHandler.Handle));

        code.ShouldNotContain("IServiceProvider services");
        code.IndexOf("serviceScope =", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("// PRIMED", StringComparison.Ordinal));
    }

    [Fact]
    public void postprocessors_are_handed_the_scoped_provider()
    {
        theServices.AddScoped<IWidget, AWidget>();
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());
        thePostProcessors.Add(() => new ProviderConsumingPostprocessor());

        var code = generate<OpaqueOnlyHandler>(nameof(OpaqueOnlyHandler.Handle));

        code.ShouldContain("var scopedThing = serviceScope.ServiceProvider;");
    }

    // Control: a method that can build everything inline creates no scope, so nothing is attached and
    // the generated code is exactly what it was before any postprocessor was registered.
    [Fact]
    public void nothing_is_emitted_when_no_scope_is_created()
    {
        theServices.AddScoped<IWidget, AWidget>();
        thePostProcessors.Add(() => new LinePostprocessor("// PRIMED"));

        var code = generate<InlineOnlyHandler>(nameof(InlineOnlyHandler.Handle));

        code.ShouldNotContain("// PRIMED");
        code.ShouldNotContain("serviceScope");
    }

    // Each generated method gets its own postprocessor instances, because those frames hold per-method
    // variable state. Two methods off one source must not share a frame.
    [Fact]
    public void a_fresh_postprocessor_is_built_for_each_method()
    {
        var built = new List<SyncFrame>();
        theServices.AddScoped<IWidget, AWidget>();
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());

        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());
        var source = new ServiceCollectionServerVariableSource(graph);
        source.ScopePostProcessorSources.Add(() =>
        {
            var frame = new LinePostprocessor("// PRIMED");
            built.Add(frame);
            return frame;
        });

        // StartNewType folds into StartNewMethod, so this is two methods, not three.
        source.StartNewMethod();
        source.StartNewMethod();

        built.Count.ShouldBe(2);
        built[0].ShouldNotBeSameAs(built[1]);
    }

    // ReplaceServiceProvider means the host handed us a container it owns -- Wolverine.HTTP's
    // httpContext.RequestServices. No scope is created there, so there is nothing to prime and we must
    // not emit against someone else's container.
    [Fact]
    public void nothing_is_attached_to_an_externally_supplied_provider()
    {
        theServices.AddScoped<IWidget, AWidget>();
        theServices.AddScoped<IScopedLambda>(_ => new ScopedLambda());

        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());

        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("ExternalProviderHarness", typeof(ServiceHarness<WidgetResult>));
        var buildMethod = type.MethodFor("Build");

        var call = new MethodCall(typeof(OpaqueOnlyHandler), nameof(OpaqueOnlyHandler.Handle));
        buildMethod.Frames.Add(call);
        buildMethod.Frames.Code("return {0};", call.ReturnVariable!);

        var source = new ServiceCollectionServerVariableSource(graph);
        source.ScopePostProcessorSources.Add(() => new LinePostprocessor("// PRIMED"));
        source.StartNewType();
        source.StartNewMethod();
        source.ReplaceServiceProvider(new Variable(typeof(IServiceProvider), "externalProvider"));

        var code = assembly.GenerateCode(source);
        _output.WriteLine(code);

        code.ShouldContain("externalProvider");
        code.ShouldNotContain("// PRIMED");
        code.ShouldNotContain("CreateAsyncScope");
    }
}

public class OpaqueOnlyHandler
{
    // No IServiceProvider anywhere -- the opaque IScopedLambda is the only thing forcing the scope.
    public static WidgetResult Handle(IWidget widget, IScopedLambda opaque)
    {
        return new WidgetResult(widget);
    }
}

public class InlineOnlyHandler
{
    public static WidgetResult Handle(IWidget widget)
    {
        return new WidgetResult(widget);
    }
}
