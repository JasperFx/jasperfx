using JasperFx.Core.Filters;
using JasperFx.Core.Reflection;

namespace JasperFx.Core.TypeScanning;

public class HasAttributeFilter<T> : IFilter<Type> where T : Attribute
{
    public bool Matches(Type type)
    {
        return type.HasAttribute<T>();
    }

    public string Description => $"Has attribute {typeof(T).GetFullName()}";
}