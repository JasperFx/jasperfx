using System.Runtime.CompilerServices;

namespace Widgets1;

/// <summary>
/// Stands in for an application's composition root in the one respect that matters to
/// <c>JasperFxOptions.DetermineCallingAssembly</c>: it owns the frame the walk should resolve, in an
/// assembly that is neither the test assembly nor anything the walk filters out. Pairing it with a
/// JasperFx-owned frame deeper in the stack (e.g. a callback invoked through <c>Each</c>) reproduces the
/// stale-anchor layout deterministically. See the anchor commentary in
/// <c>JasperFxOptions.DetermineCallingAssembly</c>.
/// </summary>
public static class WidgetRegistrationFrame
{
    /// <summary>
    /// Invokes <paramref name="walk" /> so that this assembly — "Widgets1" — owns the calling frame.
    /// Pass a method group rather than a lambda: a lambda's closure method belongs to the caller's
    /// assembly and would put the caller straight back on top of the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Invoke<T>(Func<T> walk)
    {
        var result = walk();

        // Keeps the JIT from turning the call above into a tail call, which would drop this frame and
        // defeat the whole point of the stand-in.
        GC.KeepAlive(walk);

        return result;
    }
}
