using JasperFx.Core.Filters;
using JasperFx.Core.Reflection;

namespace JasperFx.Core.TypeScanning;

public class CanCastToFilter : IFilter<Type>
{
    private readonly Type _baseType;

    public CanCastToFilter(Type baseType)
    {
        _baseType = baseType;
    }

    public bool Matches(Type type)
    {
        return type.CanBeCastTo(_baseType);
    }

    public string Description => _baseType.IsInterface
        ? $"Implements {_baseType.FullNameInCode()}"
        : $"Inherits from {_baseType.FullNameInCode()}";
}

// Really only tested in integration with other things