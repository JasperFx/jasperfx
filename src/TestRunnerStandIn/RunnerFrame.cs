using System.Runtime.CompilerServices;

// The namespace deliberately matches this project's AssemblyName. The two stack walks this stands in for
// identify an assembly differently: JasperFxOptions.DetermineCallingAssembly reads the real assembly off the
// frame, while CallingAssembly.Find parses the stack trace as TEXT and guesses the assembly by trying to
// load dotted prefixes of the method name. The second one can only ever resolve an assembly whose name
// lines up with its namespace, so the stand-in has to line up too in order to reproduce it.
//
// That alignment is not a contrivance. xUnit's Xunit.v3 namespace does not match its xunit.v3.core
// assembly, but NUnit's NUnit.Framework namespace matches its nunit.framework assembly exactly -- so NUnit
// is a real, affected case for the text-based walk.
namespace xunit.v3.stackwalk.standin;

/// <summary>
/// Stands in for a real test runner (xUnit, NUnit, ...) in the one respect that matters to
/// <c>JasperFxOptions.DetermineCallingAssembly</c> and <c>CallingAssembly.Find</c>: it puts a frame from a
/// runner-named assembly between JasperFx and the test assembly, the way an async test fixture's runner
/// frames do. See GH-600.
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
