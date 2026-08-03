using JasperFx;
using Microsoft.Extensions.DependencyInjection;

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
}
