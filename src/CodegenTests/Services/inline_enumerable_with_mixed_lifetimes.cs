using System.Collections;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Services;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace CodegenTests.Services;

// Element set for the mixed-lifetime IEnumerable<T> fix (#375).
public interface ITestService;
public class TestSingletonService : ITestService;
public class TestScopedService : ITestService;
public class TestTransientService : ITestService;
public class TestSecondSingletonService : ITestService;

public class inline_enumerable_with_mixed_lifetimes
{
    private readonly ITestOutputHelper _output;

    public inline_enumerable_with_mixed_lifetimes(ITestOutputHelper output)
    {
        _output = output;
    }

    private static (IServiceProvider provider, ServiceContainer graph) setup(Action<ServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);

        // The setup step a host (Wolverine) calls before BuildServiceProvider so mixed-lifetime
        // singleton elements can be injected by key.
        services.AddJasperFxEnumerableSingletonSupport();

        var provider = services.BuildServiceProvider();
        return (provider, new ServiceContainer(services, provider));
    }

    private (object built, string code) build(IServiceProvider provider, ServiceContainer graph, Type collectionType)
    {
        var assembly = new GeneratedAssembly(new GenerationRules());
        var constructedType = typeof(ServiceHarness<>).MakeGenericType(collectionType);
        var type = assembly.AddType("ServiceAssertion", constructedType);
        var buildMethod = type.MethodFor("Build");

        var source = new ServiceCollectionServerVariableSource(graph);
        source.StartNewType();
        source.StartNewMethod();

        buildMethod.Frames.Code("return {0};", new Use(collectionType));

        var code = assembly.GenerateCode(source);
        _output.WriteLine(code);

        var compiler = new AssemblyGenerator();
        compiler.ReferenceAssembly(GetType().Assembly);
        var builtAssembly = compiler.Generate(code);
        var builtType = builtAssembly.ExportedTypes.Single();

        var harness = resolve(provider, builtType);
        var built = builtType.GetMethod("Build")!.Invoke(harness, null)!;
        return (built, code);
    }

    // Mirrors how MS DI constructs the generated type: honor [FromKeyedServices] on constructor
    // parameters and supply the scoped IServiceProvider where the generated code needs it.
    private static object resolve(IServiceProvider provider, Type builtType)
    {
        var ctor = builtType.GetConstructors().Single();
        var args = ctor.GetParameters().Select(p =>
        {
            var keyed = p.GetCustomAttribute<FromKeyedServicesAttribute>();
            if (keyed != null)
            {
                return provider.GetRequiredKeyedService(p.ParameterType, keyed.Key);
            }

            return p.ParameterType == typeof(IServiceProvider)
                ? provider
                : provider.GetService(p.ParameterType)!;
        }).ToArray();

        return Activator.CreateInstance(builtType, args)!;
    }

    private static Type[] typesOf(object built) =>
        ((IEnumerable)built).Cast<object>().Select(x => x.GetType()).ToArray();

    private static void registerMixed(ServiceCollection services)
    {
        services.AddSingleton<ITestService, TestSingletonService>();
        services.AddScoped<ITestService, TestScopedService>();
        services.AddTransient<ITestService, TestTransientService>();
    }

    [Fact]
    public void poc_keyed_forwarding_shares_the_singleton_instance()
    {
        var services = new ServiceCollection();
        registerMixed(services);
        services.AddKeyedSingleton<ITestService>("k_singleton",
            (sp, _) => sp.GetServices<ITestService>().First(x => x is TestSingletonService));

        var provider = services.BuildServiceProvider();

        var nonKeyed = provider.GetServices<ITestService>().OfType<TestSingletonService>().Single();
        provider.GetKeyedService<ITestService>("k_singleton").ShouldBeSameAs(nonKeyed);
    }

    [Fact]
    public void mixed_lifetimes_resolve_inline_with_parity_to_GetServices()
    {
        var (provider, graph) = setup(registerMixed);

        var (built, code) = build(provider, graph, typeof(IEnumerable<ITestService>));

        // Built inline as an array literal, not bailed to whole-collection service location.
        code.ShouldContain("new CodegenTests.Services.ITestService[]");

        var expected = provider.GetServices<ITestService>().Select(x => x.GetType()).ToArray();
        typesOf(built).ShouldBe(expected);
    }

    [Fact]
    public void singleton_element_is_the_shared_container_singleton()
    {
        var (provider, graph) = setup(registerMixed);

        var (built, _) = build(provider, graph, typeof(IEnumerable<ITestService>));

        var singletonFromArray = ((IEnumerable)built).Cast<object>().OfType<TestSingletonService>().Single();
        var singletonFromContainer = provider.GetServices<ITestService>().OfType<TestSingletonService>().Single();

        singletonFromArray.ShouldBeSameAs(singletonFromContainer);
    }

    [Theory]
    [InlineData(typeof(ITestService[]))]
    [InlineData(typeof(IEnumerable<ITestService>))]
    [InlineData(typeof(IReadOnlyList<ITestService>))]
    [InlineData(typeof(IList<ITestService>))]
    public void supports_all_collection_shapes(Type collectionType)
    {
        var (provider, graph) = setup(registerMixed);

        var (built, _) = build(provider, graph, collectionType);

        var expected = provider.GetServices<ITestService>().Select(x => x.GetType()).ToArray();
        typesOf(built).ShouldBe(expected);
    }

    [Fact]
    public void two_singletons_among_mixed_are_each_shared()
    {
        var (provider, graph) = setup(s =>
        {
            s.AddSingleton<ITestService, TestSingletonService>();
            s.AddScoped<ITestService, TestScopedService>();
            s.AddSingleton<ITestService, TestSecondSingletonService>();
            s.AddTransient<ITestService, TestTransientService>();
        });

        var (built, _) = build(provider, graph, typeof(IEnumerable<ITestService>));

        typesOf(built).ShouldBe(provider.GetServices<ITestService>().Select(x => x.GetType()).ToArray());

        var arr = ((IEnumerable)built).Cast<object>().ToArray();
        arr.OfType<TestSingletonService>().Single()
            .ShouldBeSameAs(provider.GetServices<ITestService>().OfType<TestSingletonService>().Single());
        arr.OfType<TestSecondSingletonService>().Single()
            .ShouldBeSameAs(provider.GetServices<ITestService>().OfType<TestSecondSingletonService>().Single());
    }

    [Fact]
    public void keyed_registrations_are_excluded_like_GetServices()
    {
        var (provider, graph) = setup(s =>
        {
            registerMixed(s);
            s.AddKeyedSingleton<ITestService, TestSecondSingletonService>("ignored");
        });

        var (built, _) = build(provider, graph, typeof(IEnumerable<ITestService>));

        typesOf(built).ShouldNotContain(typeof(TestSecondSingletonService));
        typesOf(built).ShouldBe(provider.GetServices<ITestService>().Select(x => x.GetType()).ToArray());
    }

    [Fact]
    public void all_transient_still_inlines_without_regression()
    {
        var (provider, graph) = setup(s =>
        {
            s.AddTransient<ITestService, TestTransientService>();
            s.AddTransient<ITestService, TestScopedService>();
        });

        var (built, code) = build(provider, graph, typeof(IEnumerable<ITestService>));

        code.ShouldContain("new CodegenTests.Services.ITestService[]");
        typesOf(built).ShouldBe(provider.GetServices<ITestService>().Select(x => x.GetType()).ToArray());
    }
}
