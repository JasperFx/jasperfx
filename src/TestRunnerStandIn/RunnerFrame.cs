using System.Runtime.CompilerServices;

namespace TestRunnerStandIn;

/// <summary>
/// Stands in for a real test runner (xUnit, NUnit, ...) in the one respect that matters to
/// <c>JasperFxOptions.DetermineCallingAssembly</c>: it puts a frame from a runner-named assembly between
/// JasperFx and the test assembly, the way an async test fixture's runner frames do. See GH-600.
/// </summary>
public static class RunnerFrame
{
    /// <summary>
    /// Invokes <paramref name="action" /> so that this assembly -- named "xunit.v3.stackwalk.standin" --
    /// owns the calling frame. Pass a method group rather than a lambda: a lambda's closure method belongs
    /// to the caller's assembly and would put the caller straight back on top of the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Invoke<T>(Func<T> action)
    {
        var result = action();

        // Keeps the JIT from turning the call above into a tail call, which would drop this frame and
        // defeat the whole point of the stand-in.
        GC.KeepAlive(action);

        return result;
    }
}
