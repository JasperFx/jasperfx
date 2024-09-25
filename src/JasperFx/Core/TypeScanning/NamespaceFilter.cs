using JasperFx.Core.Filters;
using JasperFx.Core.Reflection;

namespace JasperFx.Core.TypeScanning;

public class NamespaceFilter : IFilter<Type>
{
    private readonly string _ns;

    public NamespaceFilter(string @namespace)
    {
        _ns = @namespace;
    }

    public bool Matches(Type type)
    {
        return type.IsInNamespace(_ns);
    }

    public string Description => $"Is in namespace {_ns}";
}