using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;

namespace Widgets1;

/// <summary>
/// Stands in for a shared test-harness helper that builds hosts registered elsewhere: <c>Build()</c> is
/// called from this assembly, "Widgets1", so a registration deferred into <c>Build()</c> sees this frame
/// on the stack, not the frame that registered it. jasperfx#1179.
/// </summary>
public static class HostBuiltFromAnotherAssembly
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IHost Build(IHostBuilder builder)
    {
        var host = builder.Build();

        // Keeps the JIT from turning the call above into a tail call, which would drop this frame.
        GC.KeepAlive(builder);

        return host;
    }
}
