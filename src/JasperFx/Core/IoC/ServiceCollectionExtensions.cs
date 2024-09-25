using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Core.IoC;

[JasperFxIgnore]
public class ConnectedConcretions : List<Type>
{
}

public static class ServiceCollectionExtensions
{
    internal static ServiceDescriptor? TryFindDefault<T>(this IServiceCollection services)
    {
        return services.LastOrDefault(x => x.ServiceType == typeof(T));
    }
    
    /// <summary>
    ///     Create an isolated type scanning registration policy
    /// </summary>
    /// <param name="scan"></param>
    public static IServiceCollection Scan(this IServiceCollection services, Action<IAssemblyScanner> scan)
    {
        var finder = new AssemblyScanner(services);
        scan(finder);

        finder.Start();
        finder.ApplyRegistrations(services);

        return services;
    }
    
    public static IServiceCollection ToCollection(this IEnumerable<ServiceDescriptor> descriptors)
    {
        var collection = new ServiceCollection();
        collection.AddRange(descriptors);

        return collection;
    }
    
    public static bool HasScanners(this IEnumerable<ServiceDescriptor> services)
    {
        return services.Any(x => x.ServiceType == typeof(AssemblyScanner));
    }

    public static ConnectedConcretions ConnectedConcretions(this IServiceCollection services)
    {
        var concretions = services.FirstOrDefault(x => x.ServiceType == typeof(ConnectedConcretions))
            ?.ImplementationInstance as ConnectedConcretions;

        if (concretions == null)
        {
            concretions = new ConnectedConcretions();
            services.AddSingleton(concretions);
        }

        return concretions;
    }


    public static bool Matches(this ServiceDescriptor descriptor, Type serviceType, Type implementationType)
    {
        if (descriptor.ServiceType != serviceType)
        {
            return false;
        }

        if (descriptor.ImplementationType == implementationType)
        {
            return true;
        }

        return false;
    }

    public static ServiceDescriptor? AddType(this IServiceCollection services, Type serviceType, Type implementationType,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        var hasAlready = services.Any(x => x.Matches(serviceType, implementationType));
        if (!hasAlready)
        {
            var instance = new ServiceDescriptor(serviceType, implementationType, lifetime);

            services.Add(instance);

            return instance;
        }

        return null;
    }

    public static ServiceDescriptor FindDefault<T>(this IServiceCollection services)
    {
        return services.FindDefault(typeof(T));
    }

    public static ServiceDescriptor FindDefault(this IServiceCollection services, Type serviceType)
    {
        return services.LastOrDefault(x => x.ServiceType == serviceType);
    }
}