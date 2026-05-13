using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Core.IoC;

public class FindAllTypesFilter : IRegistrationConvention
{
    private readonly ServiceLifetime _lifetime;
    private readonly Type _serviceType;

    public FindAllTypesFilter(Type serviceType, ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        _serviceType = serviceType;
        _lifetime = lifetime;
    }

    [RequiresUnreferencedCode("Convention scans types reflectively for any implementor of _serviceType; discovered types and their constructors must survive trimming.")]
    [RequiresDynamicCode("Open-generic _serviceType uses GenericConnectionScanner which closes types via MakeGenericType.")]
    void IRegistrationConvention.ScanTypes(TypeSet types, IServiceCollection services)
    {
        if (_serviceType.IsOpenGeneric())
        {
            var scanner = new GenericConnectionScanner(_serviceType);
            scanner.ScanTypes(types, services);
        }
        else
        {
            types.FindTypes(TypeClassification.Concretes | TypeClassification.Closed).Where(Matches).Each(type =>
            {
                var serviceType = determineLeastSpecificButValidType(_serviceType, type);
                services.AddType(serviceType, type, _lifetime);
            });
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
        Justification = "type comes from convention-scan discovery; reachable only from ScanTypes which is annotated [RequiresUnreferencedCode]. The user invariant for convention-based registration is that discovered types' constructors survive trimming.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "Same as IL2070 — TypeExtensions.CanBeCreated has a [DAM(PublicConstructors)] constraint that propagates from the (un-annotated) convention-scan discovery surface.")]
    private bool Matches(Type type)
    {
        return type.CanBeCastTo(_serviceType) && type.GetConstructors().Any() && type.CanBeCreated();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "type.FindFirstInterfaceThatCloses needs [DAM(Interfaces)]; type comes from convention-scan discovery. See Matches for the matching convention-scan justification.")]
    private static Type determineLeastSpecificButValidType(Type pluginType, Type type)
    {
        if (pluginType.IsGenericTypeDefinition && !type.IsOpenGeneric())
        {
            return type.FindFirstInterfaceThatCloses(pluginType);
        }

        return pluginType;
    }

    public override string ToString()
    {
        return "Find and register all types implementing " + _serviceType.FullName;
    }
}