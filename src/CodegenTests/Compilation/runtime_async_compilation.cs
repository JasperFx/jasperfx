using System;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.RuntimeCompiler;
using Shouldly;
using Xunit;

namespace CodegenTests.Compilation;

// jasperfx#1040
public class runtime_async_compilation
{
    private const string Code = """
        using System.Threading.Tasks;
        public static class Handler
        {
            public static async Task<int> Go(int x)
            {
                await Task.Yield();
                return x + 1;
            }
        }
        """;

    private static MethodInfo compile(bool runtimeAsync)
    {
        var generator = new AssemblyGenerator { UseRuntimeAsync = runtimeAsync };
        generator.ReferenceAssembly(typeof(Task).Assembly);
        return generator.Generate(Code).GetType("Handler")!.GetMethod("Go")!;
    }

    private static bool isRuntimeAsync(MethodInfo method) =>
        ((int)method.GetMethodImplementationFlags() & 0x2000) != 0;

    [Fact]
    public async Task off_by_default_uses_a_state_machine_and_runs()
    {
        var method = compile(false);
        isRuntimeAsync(method).ShouldBeFalse();
        method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>().ShouldNotBeNull();
        (await (Task<int>)method.Invoke(null, [1])!).ShouldBe(2);
    }

    [Fact]
    public async Task opt_in_is_runtime_async_on_net11_and_ignored_elsewhere()
    {
        var method = compile(true);
#if NET11_0_OR_GREATER
        isRuntimeAsync(method).ShouldBeTrue();
        method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>().ShouldBeNull();
#else
        isRuntimeAsync(method).ShouldBeFalse();
#endif
        (await (Task<int>)method.Invoke(null, [1])!).ShouldBe(2);
    }

    [Fact]
    public void reuse_key_differs_when_runtime_async_is_on()
    {
        AssemblyGenerator.ReuseKey("x", [], true).ShouldNotBe(AssemblyGenerator.ReuseKey("x", [], false));
    }
}
