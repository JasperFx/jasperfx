#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace JasperFx.Core.Reflection;

/// <summary>
/// Hot-path escape hatch for callers that today use
/// <see cref="TypeExtensions.CloseAndBuildAs{T}(Type, object[], Type[])"/> or
/// raw <see cref="Type.MakeGenericType"/> + <see cref="Activator.CreateInstance(Type, object?[])"/>
/// per invocation. Both operations are AOT-unfriendly *and* allocate-heavy at
/// steady state.
/// </summary>
/// <remarks>
/// <para>
///     <see cref="Build{T}(Type, Type, object?[])"/> caches by
///     <c>(openType, typeArgs)</c>: the first call performs the
///     <see cref="Type.MakeGenericType"/> walk and compiles a delegate that
///     invokes the matching constructor on the closed type via
///     <see cref="Expression.Lambda{TDelegate}(Expression, ParameterExpression[])"/>.
///     Subsequent calls re-use the cached delegate and hit only a
///     <see cref="ConcurrentDictionary{TKey,TValue}"/> lookup.
/// </para>
/// <para>
///     The cache is process-wide and unbounded; intended for the
///     "small finite set of closed types we'll keep using forever" shape
///     (e.g. one entry per registered document type for Marten's LINQ
///     handlers). Don't feed it user-generated open type tuples.
/// </para>
/// </remarks>
public static class GenericFactoryCache
{
    private static readonly ConcurrentDictionary<(Type Open, Type Arg, int CtorArity), Func<object?[], object>> _cache1 = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg1, Type Arg2, int CtorArity), Func<object?[], object>> _cache2 = new();

    /// <summary>
    /// Construct an instance of <c>openType&lt;arg&gt;</c> using the constructor
    /// whose arity matches <paramref name="ctorArgs"/>. Returns the result cast
    /// to <typeparamref name="T"/>.
    /// </summary>
    public static T Build<T>(Type openType, Type arg, params object?[] ctorArgs)
    {
        var factory = _cache1.GetOrAdd(
            (openType, arg, ctorArgs.Length),
            static key => BuildFactory(key.Open.MakeGenericType(key.Arg), key.CtorArity));
        return (T)factory(ctorArgs);
    }

    /// <summary>
    /// Two-type-arg overload — construct <c>openType&lt;arg1, arg2&gt;</c>.
    /// </summary>
    public static T Build<T>(Type openType, Type arg1, Type arg2, params object?[] ctorArgs)
    {
        var factory = _cache2.GetOrAdd(
            (openType, arg1, arg2, ctorArgs.Length),
            static key => BuildFactory(key.Open.MakeGenericType(key.Arg1, key.Arg2), key.CtorArity));
        return (T)factory(ctorArgs);
    }

    private static Func<object?[], object> BuildFactory(Type closedType, int ctorArity)
    {
        var ctor = closedType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == ctorArity);

        if (ctor is null)
        {
            // Caller asked for a no-arg ctor when the type doesn't have one,
            // or arity didn't match — fall back to Activator (which produces
            // the same MissingMethodException Activator would have thrown
            // before this cache existed).
            return args => Activator.CreateInstance(closedType, args)!;
        }

        // (object?[] args) => (object) new ClosedType((TArg0)args[0], (TArg1)args[1], ...)
        var argsParam = Expression.Parameter(typeof(object?[]), "args");
        var parameters = ctor.GetParameters();
        var ctorArgExprs = new Expression[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var indexer = Expression.ArrayIndex(argsParam, Expression.Constant(i));
            ctorArgExprs[i] = Expression.Convert(indexer, parameters[i].ParameterType);
        }

        var newExpr = Expression.New(ctor, ctorArgExprs);
        var castToObject = Expression.Convert(newExpr, typeof(object));
        return Expression.Lambda<Func<object?[], object>>(castToObject, argsParam).Compile();
    }
}
