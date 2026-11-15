using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

public class InjectedSingleton : InjectedField
{
    public ServiceDescriptor Descriptor { get; }
    private bool _isOnlyOne;

    public InjectedSingleton(ServiceDescriptor descriptor) : base(descriptor.ServiceType)
    {
        Descriptor = descriptor;
    }

    public override string CtorArgDeclaration()
    {
        if (Descriptor.IsKeyedService)
        {
            // The registration's own key, with its own type: "A" does not resolve a service keyed by Kind.A (#1189)
            return $"[{typeof(FromKeyedServicesAttribute).FullNameInCode().Replace("Attribute", "")}({ServiceKeys.WriteAttributeArgument(Descriptor.ServiceKey)})] " +
                   base.CtorArgDeclaration();
        }
        
        return base.CtorArgDeclaration();
    }

    public bool IsOnlyOne
    {
        private get => _isOnlyOne;
        set
        {
            _isOnlyOne = value;
            if (value)
            {
                var defaultArgName = DefaultArgName(VariableType);
                OverrideName("_" + defaultArgName);
                CtorArg = defaultArgName;
            }
        }
    }

    protected bool Equals(InjectedSingleton other)
    {
        return base.Equals(other) && Descriptor.Equals(other.Descriptor);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((InjectedSingleton)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(base.GetHashCode(), Descriptor);
    }
}