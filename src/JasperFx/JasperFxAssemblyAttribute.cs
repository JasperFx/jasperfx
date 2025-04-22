using JasperFx.CommandLine;
using JasperFx.Core.Reflection;

namespace JasperFx;

/// <summary>
/// Tells JasperFx and the "Critter Stack" frameworks that this assembly should be
/// examined for commands and possible extensions
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class JasperFxAssemblyAttribute : Attribute
{
    public JasperFxAssemblyAttribute()
    {
    }

    /// <summary>
    ///     Concrete type implementing the IServiceRegistrations interface that should
    ///     automatically be applied to hosts during environment checks or resource
    ///     commands
    /// </summary>
    /// <param name="extensionType"></param>
    public JasperFxAssemblyAttribute(Type extensionType)
    {
        if (extensionType.HasDefaultConstructor() && extensionType.CanBeCastTo<IServiceRegistrations>())
        {
            ExtensionType = extensionType;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(extensionType),
                $"Extension types must have a default, no arg constructor and implement the {nameof(IServiceRegistrations)} interface");
        }
    }

    public Type? ExtensionType { get; set; }
}

