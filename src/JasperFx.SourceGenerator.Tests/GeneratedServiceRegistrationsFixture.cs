using JasperFx.SourceGenerator.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Generated;

// Hand-written stand-in for the source-generated registrations class that
// JasperFx.SourceGenerator emits into a consuming assembly. The test project references the
// generator as a normal assembly (not an analyzer), so the generator does not run against it and
// this type does not collide. It lets RegisterAllDiscoveredServicesTests exercise the runtime
// aggregator's reflection (GetType/GetMethod/Invoke) against a real loaded assembly.
internal static class GeneratedServiceRegistrations
{
    public static void Register(IServiceCollection services)
    {
        services.Add(new ServiceDescriptor(
            typeof(IAggregatorProbe), typeof(AggregatorProbe), ServiceLifetime.Singleton));
    }
}
