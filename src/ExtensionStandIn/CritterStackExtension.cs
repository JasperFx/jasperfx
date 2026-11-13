using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ExtensionStandIn;

/// <summary>
/// Stands in for a Critter Stack extension in the one respect that matters to
/// <c>JasperFxOptions.DetermineCallingAssembly</c>: it calls <c>AddJasperFx()</c> from inside its own
/// assembly on the application's behalf, exactly the way <c>UseWolverine()</c> and <c>AddMarten()</c> do.
/// This assembly is named "Wolverine.StackWalkStandIn" so the walk sees it as framework code. See GH-601.
/// </summary>
public static class CritterStackExtension
{
    public static IServiceCollection AddSomeCritterStackTool(this IServiceCollection services)
    {
        return services.AddJasperFx();
    }

    /// <summary>
    /// Defers <c>AddJasperFx()</c> into a <c>ConfigureServices</c> callback that runs inside <c>Build()</c>,
    /// the way <c>IHostBuilder.UseWolverine()</c> does, and leaves JasperFx to walk the stack then.
    /// jasperfx#1179.
    /// </summary>
    public static IHostBuilder UseSomeCritterStackToolDeferred(this IHostBuilder builder)
    {
        return builder.ConfigureServices(services => services.AddJasperFx());
    }

    /// <summary>
    /// The same deferral, but the entry point captures the assembly it was called from before deferring
    /// and hands it to <c>AddJasperFx</c>. jasperfx#1179.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IHostBuilder UseSomeCritterStackToolCapturingCaller(this IHostBuilder builder)
    {
        var registeredFrom = Assembly.GetCallingAssembly();
        return builder.ConfigureServices(services => services.AddJasperFx(null, registeredFrom));
    }
}
