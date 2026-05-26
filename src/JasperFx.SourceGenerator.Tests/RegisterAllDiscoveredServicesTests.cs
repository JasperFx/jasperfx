using System.Linq;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

// Probe types the simulated generated registrations class (see GeneratedServiceRegistrationsFixture)
// registers, so the runtime aggregator can be exercised without this (ineligible) test assembly
// actually triggering the generator.
public interface IAggregatorProbe;

public class AggregatorProbe : IAggregatorProbe;

public class RegisterAllDiscoveredServicesTests
{
    [Fact]
    public void invokes_generated_register_for_the_supplied_assembly()
    {
        var services = new ServiceCollection();

        GeneratedExtensionManifest.RegisterAllDiscoveredServices(
            services, new[] { typeof(RegisterAllDiscoveredServicesTests).Assembly });

        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAggregatorProbe));
        descriptor.ShouldNotBeNull();
        descriptor!.ImplementationType.ShouldBe(typeof(AggregatorProbe));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void any_service_registrations_present_detects_the_generated_class()
    {
        // The fixture declares JasperFx.Generated.GeneratedServiceRegistrations in this loaded assembly.
        GeneratedExtensionManifest.AnyServiceRegistrationsPresent().ShouldBeTrue();
    }

    [Fact]
    public void no_op_when_assembly_has_no_generated_registrations()
    {
        var services = new ServiceCollection();

        // typeof(object).Assembly (System.Private.CoreLib) carries no generated registrations.
        GeneratedExtensionManifest.RegisterAllDiscoveredServices(
            services, new[] { typeof(object).Assembly });

        services.ShouldBeEmpty();
    }
}
