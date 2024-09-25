using System.Reflection;

namespace JasperFx.Core.IoC;

public static class AssemblyLoader
{
    public static Assembly ByName(string assemblyName)
    {
        return Assembly.Load(new AssemblyName(assemblyName));
    }
}