using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#1180: JasperFx.Events is compiled with runtime-async on net11.0 (JasperFxRuntimeAsync in
/// Directory.Build.props) and without it on net10.0. CodegenTests' runtime_async_defaults covers JasperFx and
/// JasperFx.RuntimeCompiler the same way.
/// </summary>
public class RuntimeAsyncDefaultTests
{
    // MethodImplAttributes.Async, the flag the compiler sets on a runtime-async method
    private const MethodImplAttributes AsyncFlag = (MethodImplAttributes)0x2000;

    [Fact]
    public void jasperfx_events_is_compiled_with_runtime_async_only_on_net11()
    {
        var types = typeof(IEvent).Assembly.GetTypes();

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
    private static bool isAsyncIteratorStateMachine(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>));
}
