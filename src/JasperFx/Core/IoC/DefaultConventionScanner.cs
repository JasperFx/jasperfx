using System.Diagnostics.CodeAnalysis;
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

    [RequiresUnreferencedCode("Convention scans types reflectively and constructs ServiceDescriptors; discovered types and their constructors must survive trimming.")]
    [RequiresDynamicCode("Inherits the contract of IRegistrationConvention.ScanTypes.")]
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

    [UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
        Justification = "Reads concreteType's interface list to find the matching I{Name} interface. Reachable only from ScanTypes which is annotated [RequiresUnreferencedCode]; trim-conscious callers either avoid convention scanning entirely or accept that registered types' interfaces survive trimming.")]
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