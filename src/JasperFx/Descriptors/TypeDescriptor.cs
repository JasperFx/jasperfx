namespace JasperFx.Descriptors;

/// <summary>
/// Just a DTO that represents a .NET Type. This is strictly used in diagnostics
/// </summary>
/// <param name="Name"></param>
/// <param name="FullName"></param>
/// <param name="AssemblyName"></param>
public record TypeDescriptor(string Name, string FullName, string AssemblyName)
{
    // FullName is null for open generics' type parameters; fall back to Name
    // rather than poisoning a diagnostics DTO with a null FullName
    public static TypeDescriptor For(Type type) =>
        new TypeDescriptor(type.Name, type.FullName ?? type.Name, type.Assembly.GetName().Name ?? string.Empty);

    public override string ToString()
    {
        return FullName;
    }
}