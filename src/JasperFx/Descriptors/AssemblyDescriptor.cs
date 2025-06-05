using System.Reflection;

namespace JasperFx.Descriptors;

/// <summary>
/// Just a DTO that represents an Assembly reference for diagnostics
/// </summary>
/// <param name="Name"></param>
/// <param name="Version"></param>
public record AssemblyDescriptor(string Name, Version Version)
{
    public static AssemblyDescriptor For(Assembly assembly) =>
        new AssemblyDescriptor(assembly.GetName().Name!, assembly.GetName().Version!);

    public override string ToString()
    {
        return $"{Name} {Version}";
    }
}