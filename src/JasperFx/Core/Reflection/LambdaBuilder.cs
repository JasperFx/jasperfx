using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;

namespace JasperFx.Core.Reflection;

/// <summary>
///     Can be used to create compiled Func's to retrieve a value from the TTarget type
/// </summary>
/// <remarks>
///     Every method here builds an expression tree and compiles it via
///     <see cref="ExpressionCompiler.CompileFast{TDelegate}"/>, which the .NET
///     trimmer cannot statically analyse. AOT-publishing apps should source-generate
///     accessor delegates rather than route through LambdaBuilder.
/// </remarks>
public static class LambdaBuilder
{
    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    public static Func<TTarget, TProperty> GetProperty<TTarget, TProperty>(PropertyInfo property)
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

    /// <summary>
    ///     Create an Action to set a property on the type TTarget
    /// </summary>
    /// <param name="property"></param>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TProperty"></typeparam>
    /// <returns></returns>
    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    public static Action<TTarget, TProperty>? SetProperty<TTarget, TProperty>(PropertyInfo property)
    {
        var target = Expression.Parameter(typeof(TTarget), "target");
        var value = Expression.Parameter(typeof(TProperty), "value");

        var method = property.SetMethod;

        if (method == null)
        {
            return null;
        }

        Expression actualValue = property.PropertyType == typeof(TProperty)
            ? value
            : Expression.Convert(value, property.PropertyType);

        var callSetMethod = Expression.Call(target, method, actualValue);

        var lambda = Expression.Lambda<Action<TTarget, TProperty>>(callSetMethod, target, value);

        return lambda.CompileFast();
    }


    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    public static Func<TTarget, TField> GetField<TTarget, TField>(FieldInfo field)
    {
        var target = Expression.Parameter(typeof(TTarget), "target");

        var fieldAccess = Expression.Field(target, field);

        var lambda = field.FieldType == typeof(TField)
            ? Expression.Lambda<Func<TTarget, TField>>(fieldAccess, target)
            : Expression.Lambda<Func<TTarget, TField>>(Expression.Convert(fieldAccess, typeof(TField)), target);

        return lambda.CompileFast();
    }

    [RequiresUnreferencedCode("Delegates to GetProperty / GetField which compile expression trees via FastExpressionCompiler.")]
    public static Func<TTarget, TMember> Getter<TTarget, TMember>(MemberInfo member)
    {
        return member is PropertyInfo
            ? GetProperty<TTarget, TMember>(member.As<PropertyInfo>())
            : GetField<TTarget, TMember>(member.As<FieldInfo>());
    }


    [RequiresUnreferencedCode("Compiles an expression tree via FastExpressionCompiler; the trimmer cannot reason about types reached only through the resulting delegate.")]
    public static Action<TTarget, TField> SetField<TTarget, TField>(FieldInfo field)
    {
        var target = Expression.Parameter(typeof(TTarget), "target");
        var value = Expression.Parameter(typeof(TField), "value");

        var fieldAccess = Expression.Field(target, field);
        var fieldSetter = Expression.Assign(fieldAccess, value);

        var lambda = Expression.Lambda<Action<TTarget, TField>>(fieldSetter, target, value);

        return lambda.CompileFast();
    }


    [RequiresUnreferencedCode("Delegates to SetProperty / SetField which compile expression trees via FastExpressionCompiler.")]
    public static Action<TTarget, TMember>? Setter<TTarget, TMember>(MemberInfo member)
    {
        return member is PropertyInfo
            ? SetProperty<TTarget, TMember>(member.As<PropertyInfo>())
            : SetField<TTarget, TMember>(member.As<FieldInfo>());
    }
}