namespace JasperFx.Descriptors;

/// <summary>
/// Just a DTO that represents a .NET Type. This is strictly used in diagnostics
/// </summary>
/// <param name="Name"></param>
/// <param name="FullName"></param>
/// <param name="AssemblyName"></param>
public record TypeDescriptor(string Name, string FullName, string AssemblyName)
{
    public static TypeDescriptor For(Type type) =>
        new TypeDescriptor(type.Name, type.FullName, type.Assembly.GetName().Name);

    public override string ToString()
    {
        return FullName;
    }
}