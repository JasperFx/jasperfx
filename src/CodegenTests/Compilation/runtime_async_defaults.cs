using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using Shouldly;
using Xunit;

namespace CodegenTests.Compilation;

/// <summary>
/// jasperfx#1180, plan decision D4: runtime-async is on by default for the net11.0 build, both for JasperFx's own
/// assemblies (JasperFxRuntimeAsync in Directory.Build.props) and for runtime-compiled code (GenerationRules.UseRuntimeAsync).
/// net10.0 cannot load runtime-async code, so it stays off there. Modeled on Wolverine's runtime_async_defaults_5150.
/// </summary>
public class runtime_async_defaults
{
    // MethodImplAttributes.Async, the flag the compiler sets on a runtime-async method
    private const MethodImplAttributes AsyncFlag = (MethodImplAttributes)0x2000;

    [Fact]
    public void generated_code_uses_runtime_async_by_default_only_on_net11()
    {
#if NET11_0_OR_GREATER
        new GenerationRules().UseRuntimeAsync.ShouldBeTrue();
        new GenerationRules("Foo").UseRuntimeAsync.ShouldBeTrue();
#else
        new GenerationRules().UseRuntimeAsync.ShouldBeFalse();
        new GenerationRules("Foo").UseRuntimeAsync.ShouldBeFalse();
#endif
    }

    // JasperFx.RuntimeCompiler has no async methods, so there is nothing to check there. EventTests' RuntimeAsyncDefaultTests
    // checks JasperFx.Events.
    [Fact]
    public void the_jasperfx_assembly_is_compiled_with_runtime_async_only_on_net11()
    {
        var types = typeof(GenerationRules).Assembly.GetTypes();

        var runtimeAsyncMethods = types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Count(m => (m.MethodImplementationFlags & AsyncFlag) != 0);

        var stateMachines = types
            .Where(t => typeof(IAsyncStateMachine).IsAssignableFrom(t) && t.IsDefined(typeof(CompilerGeneratedAttribute)))
            .ToArray();

#if NET11_0_OR_GREATER
        runtimeAsyncMethods.ShouldBeGreaterThan(0);

        // Async iterators are not converted by runtime-async, so only their state machines remain
        stateMachines.Where(t => !isAsyncIteratorStateMachine(t)).ShouldBeEmpty();
#else
        runtimeAsyncMethods.ShouldBe(0);
        stateMachines.ShouldNotBeEmpty();
#endif
    }

    // An async iterator's state machine also implements IAsyncEnumerator<T>
    private static bool isAsyncIteratorStateMachine(System.Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType &&
                                      i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IAsyncEnumerator<>));
}
