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

    private bool Matches(Type type)
    {
        return type.CanBeCastTo(_serviceType) && type.GetConstructors().Any() && type.CanBeCreated();
    }

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