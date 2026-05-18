using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using FastExpressionCompiler;

namespace JasperFx.Core.Reflection;

/// <summary>
///     Can be used to create compiled Func's to retrieve a value from the TTarget type
/// </summary>
/// <remarks>
///     Each method branches at runtime on <see cref="RuntimeFeature.IsDynamicCodeSupported"/>:
///     on a JIT runtime (the common case) the implementation compiles an expression tree
///     via <c>ExpressionCompiler.CompileFast</c> for near-direct call
///     performance. When dynamic code generation is unavailable (NativeAOT-published
///     applications), it falls back to plain reflection-based <c>GetValue</c> /
///     <c>SetValue</c> calls, which are slower but trim- and AOT-safe.
///     The methods are still annotated <see cref="RequiresUnreferencedCodeAttribute"/>
///     because the JIT branch can still surface trim-only members via the resulting
///     delegate; AOT callers do not require <see cref="RequiresDynamicCodeAttribute"/>
///     opt-in any more.
/// </remarks>
public static class LambdaBuilder
{
    [RequiresUnreferencedCode("On a JIT runtime, compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate. Under NativeAOT the reflective fallback is trim-safe so this warning is conservative.")]
    public static Func<TTarget, TProperty> GetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return CompiledGetProperty<TTarget, TProperty>(property);
        }

        return ReflectiveGetProperty<TTarget, TProperty>(property);
    }

    /// <summary>
    ///     Create an Action to set a property on the type TTarget
    /// </summary>
    [RequiresUnreferencedCode("On a JIT runtime, compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate. Under NativeAOT the reflective fallback is trim-safe so this warning is conservative.")]
    public static Action<TTarget, TProperty>? SetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        if (property.SetMethod == null)
        {
            return null;
        }

        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return CompiledSetProperty<TTarget, TProperty>(property);
        }

        return ReflectiveSetProperty<TTarget, TProperty>(property);
    }

    [RequiresUnreferencedCode("On a JIT runtime, compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate. Under NativeAOT the reflective fallback is trim-safe so this warning is conservative.")]
    public static Func<TTarget, TField> GetField<TTarget, TField>(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return CompiledGetField<TTarget, TField>(field);
        }

        return ReflectiveGetField<TTarget, TField>(field);
    }

    [RequiresUnreferencedCode("Delegates to GetProperty / GetField; see those methods for runtime behaviour.")]
    public static Func<TTarget, TMember> Getter<TTarget, TMember>(MemberInfo member)
    {
        return member is PropertyInfo
            ? GetProperty<TTarget, TMember>(member.As<PropertyInfo>())
            : GetField<TTarget, TMember>(member.As<FieldInfo>());
    }

    [RequiresUnreferencedCode("On a JIT runtime, compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate. Under NativeAOT the reflective fallback is trim-safe so this warning is conservative.")]
    public static Action<TTarget, TField> SetField<TTarget, TField>(FieldInfo field)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return CompiledSetField<TTarget, TField>(field);
        }

        return ReflectiveSetField<TTarget, TField>(field);
    }

    [RequiresUnreferencedCode("Delegates to SetProperty / SetField; see those methods for runtime behaviour.")]
    public static Action<TTarget, TMember>? Setter<TTarget, TMember>(MemberInfo member)
    {
        return member is PropertyInfo
            ? SetProperty<TTarget, TMember>(member.As<PropertyInfo>())
            : SetField<TTarget, TMember>(member.As<FieldInfo>());
    }

    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private static Func<TTarget, TProperty> CompiledGetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        var target = Expression.Parameter(property.DeclaringType!, "target");
        var method = property.GetGetMethod()!;

        var callGetMethod = Expression.Call(target, method);

        var lambda = method.ReturnType == typeof(TProperty)
            ? Expression.Lambda<Func<TTarget, TProperty>>(callGetMethod, target)
            : Expression.Lambda<Func<TTarget, TProperty>>(Expression.Convert(callGetMethod, typeof(TProperty)),
                target);

        return lambda.CompileFast();
    }

    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private static Action<TTarget, TProperty> CompiledSetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        var target = Expression.Parameter(typeof(TTarget), "target");
        var value = Expression.Parameter(typeof(TProperty), "value");

        var method = property.SetMethod!;

        Expression actualValue = property.PropertyType == typeof(TProperty)
            ? value
            : Expression.Convert(value, property.PropertyType);

        var callSetMethod = Expression.Call(target, method, actualValue);

        var lambda = Expression.Lambda<Action<TTarget, TProperty>>(callSetMethod, target, value);

        return lambda.CompileFast();
    }

    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private static Func<TTarget, TField> CompiledGetField<TTarget, TField>(FieldInfo field)
    {
        var target = Expression.Parameter(typeof(TTarget), "target");

        var fieldAccess = Expression.Field(target, field);

        var lambda = field.FieldType == typeof(TField)
            ? Expression.Lambda<Func<TTarget, TField>>(fieldAccess, target)
            : Expression.Lambda<Func<TTarget, TField>>(Expression.Convert(fieldAccess, typeof(TField)), target);

        return lambda.CompileFast();
    }

    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private static Action<TTarget, TField> CompiledSetField<TTarget, TField>(FieldInfo field)
    {
        var target = Expression.Parameter(typeof(TTarget), "target");
        var value = Expression.Parameter(typeof(TField), "value");

        var fieldAccess = Expression.Field(target, field);
        var fieldSetter = Expression.Assign(fieldAccess, value);

        var lambda = Expression.Lambda<Action<TTarget, TField>>(fieldSetter, target, value);

        return lambda.CompileFast();
    }

    private static Func<TTarget, TProperty> ReflectiveGetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        return target =>
        {
            var raw = property.GetValue(target);
            return (TProperty)raw!;
        };
    }

    private static Action<TTarget, TProperty> ReflectiveSetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        return (target, value) => property.SetValue(target, value);
    }

    private static Func<TTarget, TField> ReflectiveGetField<TTarget, TField>(FieldInfo field)
    {
        return target =>
        {
            var raw = field.GetValue(target);
            return (TField)raw!;
        };
    }

    private static Action<TTarget, TField> ReflectiveSetField<TTarget, TField>(FieldInfo field)
    {
        return (target, value) => field.SetValue(target, value);
    }
}
