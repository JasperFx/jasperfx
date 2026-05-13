using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

internal class ServiceProviderFamily : ServiceFamily
{
    public ServiceProviderFamily() : base(typeof(IServiceProvider), Array.Empty<ServiceDescriptor>())
    {

    }

    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers",
        Justification = "The placeholder ServiceDescriptor here uses typeof(ServiceDescriptor) as a stand-in implementation type only to satisfy the ServiceDescriptor ctor's non-null contract — ServiceProviderPlan.CreateVariable throws NotImplementedException and the descriptor is never actually instantiated. The real IServiceProvider is bound by the host's DI container.")]
    public override ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        return new ServiceProviderPlan(new ServiceDescriptor(typeof(IServiceProvider), typeof(ServiceDescriptor),
            ServiceLifetime.Scoped));
    }
}

internal class ServiceScopeFactoryFamily : ServiceFamily
{
    public ServiceScopeFactoryFamily() : base(typeof(IServiceScopeFactory), Array.Empty<ServiceDescriptor>())
    {

    }

    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers",
        Justification = "The placeholder ServiceDescriptor here uses typeof(ServiceDescriptor) as a stand-in implementation type only to satisfy the ServiceDescriptor ctor's non-null contract — ServiceProviderPlan.CreateVariable throws NotImplementedException and the descriptor is never actually instantiated. The real IServiceScopeFactory is bound by the host's DI container.")]
    public override ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        return new ServiceProviderPlan(new ServiceDescriptor(typeof(IServiceScopeFactory), typeof(ServiceDescriptor),
            ServiceLifetime.Singleton));
    }
}

internal class ServiceProviderPlan : ServicePlan
{
    public ServiceProviderPlan(ServiceDescriptor descriptor) : base(descriptor)
    {
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return true;
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        return "Your code is directly using IServiceProvider";
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        throw new NotImplementedException();
    }
}