using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

internal class ServiceFamily
{
    public Type ServiceType { get; }
    public IReadOnlyList<ServiceDescriptor> Services { get; }

    public ServiceFamily(Type serviceType, IEnumerable<ServiceDescriptor> services)
    {
        ServiceType = serviceType;
        Services = services.ToArray();
    }

    public ServiceDescriptor? Default => Services.LastOrDefault(x => !x.IsKeyedService);

    [RequiresUnreferencedCode("Closes an open-generic ServiceType / ImplementationType via MakeGenericType using user-supplied parameter types. The trimmer cannot statically reason about the closed shape; consumers depending on this path either accept the warning or replace open-generic registration with closed-generic / source-generated alternatives.")]
    [RequiresDynamicCode("ServiceType.MakeGenericType + descriptor.ImplementationType.MakeGenericType require runtime code generation.")]
    public ServiceFamily Close(Type[] parameterTypes)
    {
        if (!ServiceType.IsOpenGeneric())
            throw new InvalidOperationException($"{ServiceType.FullNameInCode()} is not an open type");
        var serviceType = ServiceType.MakeGenericType(parameterTypes);

        var candidates = Services.Where(x => x.ImplementationType != null).Select(open =>
        {
            try
            {
                var concreteType = open.ImplementationType!.MakeGenericType(parameterTypes);
                if (concreteType.CanBeCastTo(serviceType))
                {
                    return new ServiceDescriptor(serviceType, concreteType, open.Lifetime);
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }).Where(x => x != null).ToArray();

        return new ServiceFamily(serviceType, candidates);
    }

    public virtual ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        var descriptor = Services.LastOrDefault();
        if (descriptor == null) return null;
        
        return BuildPlan(graph, descriptor, trail);
    }

    internal ServicePlan BuildPlan(ServiceContainer graph, ServiceDescriptor descriptor,
        List<ServiceDescriptor> trail)
    {
        if (trail.Contains(descriptor)) return new InvalidPlan(descriptor);
        
        if (descriptor.ServiceType.IsNotPublic)
        {
            return new ServiceLocationPlan(descriptor);
        }

        if (descriptor.Lifetime == ServiceLifetime.Singleton)
        {
            return new SingletonPlan(descriptor);
        }

        if (descriptor.IsKeyedService)
        {
            if (descriptor.KeyedImplementationFactory != null)
            {
                return new ServiceLocationPlan(descriptor);
            }

            if (descriptor.KeyedImplementationType.IsNotPublic)
            {
                return new ServiceLocationPlan(descriptor);
            }

            if (!descriptor.KeyedImplementationType.IsConcrete())
            {
                return new InvalidPlan(descriptor);
            }
            
            if (ConstructorPlan.TryBuildPlan(trail, descriptor, graph, out var plan2))
            {
                return plan2;
            }
        }

        if (descriptor.ImplementationFactory != null)
        {
            return new ServiceLocationPlan(descriptor);
        }
        
        
        
        if (!descriptor.ImplementationType.IsConcrete())
        {
            // If you don't know how to create it, you can't use it, period
            return new InvalidPlan(descriptor);
        }

        if (descriptor.ImplementationType.IsNotPublic)
        {
            return new ServiceLocationPlan(descriptor);
        }
        
        if (ConstructorPlan.TryBuildPlan(trail, descriptor, graph, out var plan))
        {
            return plan;
        }

        return new InvalidPlan(descriptor);
    }
}
