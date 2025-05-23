using System.Reflection;
using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Core.IoC;

internal class DefaultConventionScanner : IRegistrationConvention
{
    private readonly ServiceLifetime _lifetime;

    public DefaultConventionScanner(ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        _lifetime = lifetime;
    }

    public OverwriteBehavior Overwrites { get; set; } = OverwriteBehavior.NewType;

    public void ScanTypes(TypeSet types, IServiceCollection services)
    {
        foreach (var type in types.FindTypes(TypeClassification.Concretes)
                     .Where(type => type.GetConstructors().Any()))
        {
            var serviceType = FindServiceType(type);
            if (serviceType != null && ShouldAdd(services, serviceType, type))
            {
                services.Add(new ServiceDescriptor(serviceType, type, _lifetime));
            }
        }
    }

    public bool ShouldAdd(IServiceCollection services, Type serviceType, Type implementationType)
    {
        if (Overwrites == OverwriteBehavior.Always)
        {
            return true;
        }

        var matches = services.Where(x => x.ServiceType == serviceType).ToArray();
        if (!matches.Any())
        {
            return true;
        }

        if (Overwrites == OverwriteBehavior.Never)
        {
            return false;
        }

        var hasMatch = matches.Any(x => x.Matches(serviceType, implementationType));

        return !hasMatch;
    }

    public virtual Type? FindServiceType(Type concreteType)
    {
        var interfaceName = "I" + concreteType.Name;
        return concreteType.GetTypeInfo().GetInterfaces().FirstOrDefault(t => t.Name == interfaceName);
    }

    public override string ToString()
    {
        return "Default I[Name]/[Name] registration convention";
    }
}