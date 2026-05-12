using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodegenTests.Bugs;

/// <summary>
/// Reproduces JasperFx/wolverine#2719: when a generated method requires
/// service location (via the <c>ScopedContainerCreation</c> dependency
/// frame inserted to satisfy a service-located parameter referenced by a
/// downstream <see cref="MethodCall"/>) AND the method is otherwise fully
/// synchronous, the generated code historically combined
/// <c>await using var serviceScope</c> with a non-async method declaration
/// (no <c>async</c> keyword on the return type), producing CS4032 / CS1996
/// compile failures.
///
/// JasperFx PR #238 (commit 7dc7f0e) gated the <c>await using</c> emission
/// on <see cref="AsyncMode.AsyncTask"/>, which should keep this scenario
/// compilable — sync method body should get <c>using var scope = CreateScope()</c>
/// instead. This test verifies that gate via a full codegen → compile cycle
/// using the real <see cref="ServiceCollectionServerVariableSource"/>.
/// </summary>
public class Bug_244_scoped_session_compiles_in_non_async_method
{
    public interface IBugHandler
    {
        Task DoStuff();
    }

    public interface IServiceLocatedDependency;
    public class ServiceLocatedDependency : IServiceLocatedDependency;

    public static class ServiceConsumer
    {
        public static void UseTheService(IServiceLocatedDependency dep)
        {
            dep.ShouldNotBeNull();
        }
    }

    [Fact]
    public void compiles_when_method_is_sync_but_has_service_location()
    {
        // Opaque lambda-factory registration with Scoped lifetime — this is
        // the exact "requires service location" shape from #2719's repro
        // (`AddScoped<IGlobalUserContext>(sp => new GlobalUserContext())`).
        var services = new ServiceCollection();
        services.AddScoped<IServiceLocatedDependency>(_ => new ServiceLocatedDependency());

        var graph = new ServiceContainer(services, services.BuildServiceProvider());
        var source = new ServiceCollectionServerVariableSource(graph);
        source.StartNewType();
        source.StartNewMethod();

        var assembly = new GeneratedAssembly(new GenerationRules());
        var type = assembly.AddType("ScopedSyncHandler", typeof(IBugHandler));
        var method = type.MethodFor(nameof(IBugHandler.DoStuff));

        // The sync method call needs IServiceLocatedDependency. Because the
        // registration is an opaque lambda factory with Scoped lifetime,
        // JasperFx will resolve via ScopedContainerCreation + GetServiceFromScopedContainerFrame.
        method.Frames.Add(new MethodCall(typeof(ServiceConsumer), nameof(ServiceConsumer.UseTheService)));

        var generator = new AssemblyGenerator();
        generator.ReferenceAssembly(typeof(IServiceLocatedDependency).Assembly);
        var code = assembly.GenerateCode(source);
        generator.Generate(code);  // Throws on compile failure.

        // Method declaration must NOT have `async` since no frame is actually async.
        // Body must use sync `using` (the JasperFx#238 fix) not `await using`.
        type.SourceCode.ShouldNotBeNull();
        type.SourceCode.ShouldNotContain("async ");
        type.SourceCode.ShouldNotContain("await using");
        type.SourceCode.ShouldContain("using var serviceScope = _serviceScopeFactory.CreateScope();");
    }
}
