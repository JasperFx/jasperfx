using JasperFx.Core.Reflection;

namespace JasperFx.CommandLine;

/// <summary>
///     If the CommandExecutor is configured to discover assemblies,
///     this attribute on an assembly will cause JasperFx to search for
///     command types within this assembly
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

    public Type ExtensionType { get; set; }
}