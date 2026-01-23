using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using ImTools;

namespace JasperFx.Core.Reflection;

/// <summary>
///     Internal model of a custom "wrapped" value type the Critter Stack uses
///     for LINQ generation and any place where a value type is treated as an identifier
/// </summary>
public class ValueTypeInfo
{
    private static ImHashMap<Type, ValueTypeInfo> _valueTypes = ImHashMap<Type, ValueTypeInfo>.Empty;
    
    public static ValueTypeInfo ForType(Type type)
    {
        if (_valueTypes.TryFind(type, out var valueType)) return valueType;
        
        var valueProperty = type.GetProperties().Where(x => x.Name != "Tag").SingleOrDefaultIfMany();
        if (valueProperty == null || !valueProperty.CanRead) throw new InvalidValueTypeException(type, "Must be only a single public, 'gettable' property");

        var ctor = type.GetConstructors()
            .FirstOrDefault(x => x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == valueProperty.PropertyType);

        if (ctor != null)
        {
            valueType = new ValueTypeInfo(type, valueProperty.PropertyType, valueProperty, ctor);
            _valueTypes = _valueTypes.AddOrUpdate(type, valueType);
            return valueType;
        }

        var builder = type.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(x =>
            x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == valueProperty.PropertyType);

        if (builder != null)
        {
            valueType = new ValueTypeInfo(type, valueProperty.PropertyType, valueProperty, builder);
            Register(valueType);
            return valueType;
        }

        throw new InvalidValueTypeException(type,
            "Unable to determine either a builder static method or a constructor to use");

    }

    public static void Register(ValueTypeInfo valueType)
    {
        _valueTypes = _valueTypes.AddOrUpdate(valueType.OuterType, valueType);
    }

    private object? _converter;

    public ValueTypeInfo(Type outerType, Type simpleType, PropertyInfo valueProperty, ConstructorInfo ctor)
    {
        OuterType = outerType;
        SimpleType = simpleType;
        ValueProperty = valueProperty;
        Ctor = ctor;
    }

    public ValueTypeInfo(Type outerType, Type simpleType, PropertyInfo valueProperty, MethodInfo builder)
    {
        OuterType = outerType;
        SimpleType = simpleType;
        ValueProperty = valueProperty;
        Builder = builder;
    }

    public Type OuterType { get; }
    public Type SimpleType { get; }
    public PropertyInfo ValueProperty { get; }
    public MethodInfo? Builder { get; }
    public ConstructorInfo? Ctor { get; }

    public Func<TInner, TOuter> CreateWrapper<TOuter, TInner>()
    {
        if (_converter != null)
        {
            return (Func<TInner, TOuter>)_converter;
        }

        var inner = Expression.Parameter(typeof(TInner), "inner");
        Expression builder;
        if (Builder != null)
        {
            builder = Expression.Call(null, Builder, inner);
        }
        else if (Ctor != null)
        {
            builder = Expression.New(Ctor, inner);
        }
        else
        {
            throw new NotSupportedException("Cannot build a type converter for strong typed id type " +
                                            OuterType.FullNameInCode());
        }

        var lambda = Expression.Lambda<Func<TInner, TOuter>>(builder, inner);

        _converter = lambda.CompileFast();
        return (Func<TInner, TOuter>)_converter;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TOuter"></typeparam>
    /// <typeparam name="TInner"></typeparam>
    /// <returns></returns>
    public Func<TOuter, TInner> UnWrapper<TOuter, TInner>()
    {
        var outer = Expression.Parameter(typeof(TOuter), "outer");
        var getter = ValueProperty.GetMethod!;
        var lambda = Expression.Lambda<Func<TOuter, TInner>>(Expression.Call(outer, getter), outer);
        return lambda.CompileFast();
    }
    
}
