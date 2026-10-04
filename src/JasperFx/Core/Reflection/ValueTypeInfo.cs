using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [RequiresUnreferencedCode("Reflects over type's public properties + constructors + static factory methods to discover the strong-typed-id shape. Discovered members must survive trimming.")]
    public static ValueTypeInfo ForType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
    {
        if (_valueTypes.TryFind(type, out var valueType)) return valueType;
        
        var allProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var candidates = allProperties.Where(x => x.Name != "Tag").ToArray();

        // F# single-case discriminated unions may have Is* boolean properties (older F# compilers).
        // If "Tag" is present (strong F# DU signal) and multiple properties remain, filter those out.
        if (candidates.Length > 1 && allProperties.Any(x => x.Name == "Tag"))
        {
            candidates = candidates.Where(x => !(x.PropertyType == typeof(bool) && x.Name.StartsWith("Is"))).ToArray();
        }

        var valueProperty = candidates.SingleOrDefaultIfMany();
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

    /// <summary>
    ///     Build a delegate that wraps an inner value in the outer value type. On a JIT runtime
    ///     this compiles an expression tree; when dynamic code generation is unavailable
    ///     (Native AOT) it falls back to invoking the already-resolved constructor or builder
    ///     method through reflection.
    /// </summary>
    [RequiresUnreferencedCode("On a JIT runtime, compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate. Under NativeAOT the reflective fallback is trim-safe so this warning is conservative.")]
    public Func<TInner, TOuter> CreateWrapper<TOuter, TInner>()
    {
        if (_converter != null)
        {
            return (Func<TInner, TOuter>)_converter;
        }

        _converter = RuntimeFeature.IsDynamicCodeSupported
            ? CompiledWrapper<TOuter, TInner>()
            : ReflectiveWrapper<TOuter, TInner>();

        return (Func<TInner, TOuter>)_converter;
    }

    /// <summary>
    ///     Build a delegate that extracts the inner value from the outer value type. On a JIT
    ///     runtime this compiles an expression tree; when dynamic code generation is unavailable
    ///     (Native AOT) it falls back to reading <see cref="ValueProperty"/> through reflection.
    /// </summary>
    [RequiresUnreferencedCode("On a JIT runtime, compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate. Under NativeAOT the reflective fallback is trim-safe so this warning is conservative.")]
    public Func<TOuter, TInner> UnWrapper<TOuter, TInner>()
    {
        return RuntimeFeature.IsDynamicCodeSupported
            ? CompiledUnWrapper<TOuter, TInner>()
            : ReflectiveUnWrapper<TOuter, TInner>();
    }

    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private Func<TInner, TOuter> CompiledWrapper<TOuter, TInner>()
    {
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
        return lambda.CompileFast();
    }

    internal Func<TInner, TOuter> ReflectiveWrapper<TOuter, TInner>()
    {
        if (Builder != null)
        {
            var method = Builder;
            return inner => (TOuter)method.Invoke(null, [inner])!;
        }

        if (Ctor != null)
        {
            var ctor = Ctor;
            return inner => (TOuter)ctor.Invoke([inner]);
        }

        throw new NotSupportedException("Cannot build a type converter for strong typed id type " +
                                        OuterType.FullNameInCode());
    }

    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private Func<TOuter, TInner> CompiledUnWrapper<TOuter, TInner>()
    {
        var outer = Expression.Parameter(typeof(TOuter), "outer");
        var getter = ValueProperty.GetMethod!;
        var lambda = Expression.Lambda<Func<TOuter, TInner>>(Expression.Call(outer, getter), outer);
        return lambda.CompileFast();
    }

    internal Func<TOuter, TInner> ReflectiveUnWrapper<TOuter, TInner>()
    {
        var property = ValueProperty;
        return outer => (TInner)property.GetValue(outer)!;
    }
}
