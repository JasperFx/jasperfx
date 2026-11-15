using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Services;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodegenTests.Services;

// jasperfx#1189: a [FromKeyedServices] key that is not a string (an enum here) was turned into a string
// before the lookup, so it never matched the registration keyed by the enum value. Codegen then fell
// through, with no error, to the last unkeyed registration of the same type, and the generated
// constructor attribute named the key as a string, which would not have resolved the enum-keyed
// registration either.
public class non_string_keyed_services
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceCollection theServices = new();

    public non_string_keyed_services(ITestOutputHelper output)
    {
        _output = output;
    }

    private (string Code, ServiceContainer Graph) generate(Type handlerType, Type resultType, bool viaConstructor)
    {
        var graph = new ServiceContainer(theServices, theServices.BuildServiceProvider());

        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType(handlerType.Name + "Harness", typeof(ServiceHarness<>).MakeGenericType(resultType));
        var buildMethod = type.MethodFor("Build");

        var call = new MethodCall(handlerType, "Handle");

        if (viaConstructor)
        {
            foreach (var frame in graph.TryCreateConstructorFrames(new[] { call }))
            {
                buildMethod.Frames.Add(frame);
            }
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

    private T build<T>(string code, ServiceContainer graph) where T : class
    {
        var compiler = new AssemblyGenerator();
        compiler.ReferenceAssembly(GetType().Assembly);
        var builtType = compiler.Generate(code).ExportedTypes.Single();

        // QuickBuild honours [FromKeyedServices] on the generated constructor, as Wolverine does
        return ((ServiceHarness<T>)graph.QuickBuild(builtType)).Build();
    }

    private void registerStoresWithUnkeyedForwarders()
    {
        // The shape from the issue: opaque keyed registrations, plus unkeyed forwarders so that
        // IEnumerable<IStore> sees both. The forwarders are what the old lookup silently fell back to.
        theServices.AddKeyedSingleton<IStore>(StoreKind.A, (_, _) => new Store("A"));
        theServices.AddKeyedSingleton<IStore>(StoreKind.B, (_, _) => new Store("B"));
        theServices.AddSingleton<IStore>(p => p.GetRequiredKeyedService<IStore>(StoreKind.A));
        theServices.AddSingleton<IStore>(p => p.GetRequiredKeyedService<IStore>(StoreKind.B));
    }

    [Fact]
    public void an_enum_keyed_method_parameter_resolves_its_own_registration_at_runtime()
    {
        registerStoresWithUnkeyedForwarders();

        var (code, graph) = generate(typeof(EnumKeyedMethodHandler), typeof(StoreResult), viaConstructor: false);

        // Before the fix: `(CodegenTests.Services.IStore store)`, the key lost, resolving to "B"
        code.ShouldContain("[Microsoft.Extensions.DependencyInjection.FromKeyedServices(CodegenTests.Services.StoreKind.A)] CodegenTests.Services.IStore store");

        build<StoreResult>(code, graph).Store.Name.ShouldBe("A");
    }

    [Fact]
    public void an_enum_keyed_scoped_service_on_service_location_keeps_its_typed_key()
    {
        theServices.AddKeyedScoped<IStore>(StoreKind.A, (_, _) => new Store("A"));
        theServices.AddKeyedScoped<IStore>(StoreKind.B, (_, _) => new Store("B"));
        theServices.AddScoped<IStore>(_ => new Store("unkeyed"));

        var (code, graph) = generate(typeof(EnumKeyedMethodHandler), typeof(StoreResult), viaConstructor: false);

        code.ShouldContain("GetRequiredKeyedService<CodegenTests.Services.IStore>(serviceScope.ServiceProvider, CodegenTests.Services.StoreKind.A)");

        build<StoreResult>(code, graph).Store.Name.ShouldBe("A");
    }

    [Fact]
    public void enum_keyed_constructor_parameters_resolve_their_own_registrations_at_runtime()
    {
        registerStoresWithUnkeyedForwarders();

        var (code, graph) = generate(typeof(EnumKeyedCtorHandler), typeof(TwoStoreResult), viaConstructor: true);

        var result = build<TwoStoreResult>(code, graph);
        result.First.Name.ShouldBe("A");
        result.Second.Name.ShouldBe("B");
    }

    [Fact]
    public void an_injected_singleton_names_the_enum_key_with_its_own_type()
    {
        theServices.AddKeyedSingleton<IStore, AStore>(StoreKind.A);
        theServices.AddKeyedSingleton<IStore, BStore>(StoreKind.B);
        theServices.AddSingleton<IStore, BStore>();

        var (code, graph) = generate(typeof(EnumKeyedCtorHandler), typeof(TwoStoreResult), viaConstructor: true);

        code.ShouldContain("[Microsoft.Extensions.DependencyInjection.FromKeyedServices(CodegenTests.Services.StoreKind.A)]");
        code.ShouldContain("[Microsoft.Extensions.DependencyInjection.FromKeyedServices(CodegenTests.Services.StoreKind.B)]");
        code.ShouldNotContain("FromKeyedServices(\"A\")");

        var result = build<TwoStoreResult>(code, graph);
        result.First.ShouldBeOfType<AStore>();
        result.Second.ShouldBeOfType<BStore>();
    }

    [Fact]
    public void an_int_keyed_parameter_resolves_its_own_registration()
    {
        theServices.AddKeyedSingleton<IStore, AStore>(1);
        theServices.AddKeyedSingleton<IStore, BStore>(2);
        theServices.AddSingleton<IStore, BStore>();

        var (code, graph) = generate(typeof(IntKeyedCtorHandler), typeof(StoreResult), viaConstructor: true);

        code.ShouldContain("[Microsoft.Extensions.DependencyInjection.FromKeyedServices(1)]");
        build<StoreResult>(code, graph).Store.ShouldBeOfType<AStore>();
    }

    [Fact]
    public void a_string_key_still_resolves_and_is_written_as_a_string()
    {
        theServices.AddKeyedSingleton<IStore, AStore>("alpha");
        theServices.AddSingleton<IStore, BStore>();

        var (code, graph) = generate(typeof(StringKeyedCtorHandler), typeof(StoreResult), viaConstructor: true);

        code.ShouldContain("[Microsoft.Extensions.DependencyInjection.FromKeyedServices(\"alpha\")]");
        build<StoreResult>(code, graph).Store.ShouldBeOfType<AStore>();
    }

    [Fact]
    public void a_keyed_parameter_with_no_registration_for_its_key_fails_codegen_instead_of_taking_the_default()
    {
        // Only StoreKind.B is keyed. The unkeyed registration must not stand in for StoreKind.A.
        theServices.AddKeyedSingleton<IStore>(StoreKind.B, (_, _) => new Store("B"));
        theServices.AddSingleton<IStore>(_ => new Store("unkeyed"));

        var ex = Should.Throw<Exception>(() =>
            generate(typeof(EnumKeyedMethodHandler), typeof(StoreResult), viaConstructor: false));

        var inner = ex as InvalidOperationException ?? ex.InnerException as InvalidOperationException;
        inner.ShouldNotBeNull(ex.ToString());
        inner.Message.ShouldContain("FromKeyedServices(CodegenTests.Services.StoreKind.A)");
        inner.Message.ShouldContain("no service of that type is registered with that key");
    }
}

public enum StoreKind
{
    A = 1,
    B = 2
}

public interface IStore
{
    string Name { get; }
}

public sealed record Store(string Name) : IStore;

public class AStore : IStore
{
    public string Name => "A";
}

public class BStore : IStore
{
    public string Name => "B";
}

public record StoreResult(IStore Store);

public record TwoStoreResult(IStore First, IStore Second);

public class EnumKeyedMethodHandler
{
    public static StoreResult Handle([FromKeyedServices(StoreKind.A)] IStore store) => new(store);
}

public class EnumKeyedCtorHandler
{
    private readonly IStore _first;
    private readonly IStore _second;

    public EnumKeyedCtorHandler([FromKeyedServices(StoreKind.A)] IStore first, [FromKeyedServices(StoreKind.B)] IStore second)
    {
        _first = first;
        _second = second;
    }

    public TwoStoreResult Handle() => new(_first, _second);
}

public class IntKeyedCtorHandler
{
    private readonly IStore _store;

    public IntKeyedCtorHandler([FromKeyedServices(1)] IStore store)
    {
        _store = store;
    }

    public StoreResult Handle() => new(_store);
}

public class StringKeyedCtorHandler
{
    private readonly IStore _store;

    public StringKeyedCtorHandler([FromKeyedServices("alpha")] IStore store)
    {
        _store = store;
    }

    public StoreResult Handle() => new(_store);
}
