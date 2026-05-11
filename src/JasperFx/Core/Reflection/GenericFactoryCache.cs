using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Core.Reflection;

/// <summary>
/// Delegate-cached escape hatch for runtime generic instantiation.
///
/// <see cref="TypeExtensions.CloseAndBuildAs{T}(Type, Type[])"/> and its
/// overloads close an open generic type with <c>MakeGenericType</c> and
/// build an instance via <c>Activator.CreateInstance</c> on every call.
/// Both operations are AOT/trim-unfriendly, and the latter is reflective
/// and per-call expensive on hot paths.
///
/// Hot-path callers — for example, per-query LINQ handler construction —
/// should use this cache instead. They supply a delegate factory once
/// (which can be source-generated or hand-written) and the cache memoises
/// the constructed delegate per <c>(openType, typeArgs...)</c> tuple, so
/// steady-state calls avoid <c>Activator.CreateInstance</c> entirely.
/// </summary>
public static class GenericFactoryCache
{
    private static readonly ConcurrentDictionary<(Type Open, Type Arg), Delegate> _zeroArg = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg), Delegate> _oneArg = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg1, Type Arg2), Delegate> _twoArg = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg1, Type Arg2, Type Arg3), Delegate> _threeArg = new();

    // Extended overloads (#223). Original 4 dicts conflate type-arg count with
    // ctor-arg count; real call sites (LINQ handler builders in Marten, similar
    // patterns in Polecat/Wolverine) need (1 type, ≥2 ctor) and (2 type, 3 ctor)
    // shapes. Each new overload uses its own dictionary so delegate types don't
    // alias across shapes for the same (openType, typeArg...) key tuple.
    private static readonly ConcurrentDictionary<(Type Open, Type Arg), Delegate> _t1c2 = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg), Delegate> _t1c3 = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg1, Type Arg2), Delegate> _t2c3 = new();
    private static readonly ConcurrentDictionary<(Type Open, Type Arg), Delegate> _t1cArray = new();

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> by closing
    /// <paramref name="openType"/> with <paramref name="typeArgument"/>.
    /// The first call for a given <c>(openType, typeArgument)</c> invokes
    /// <paramref name="factoryFactory"/> to produce a constructor delegate;
    /// subsequent calls reuse the cached delegate.
    /// </summary>
    /// <param name="openType">The open generic type, e.g. <c>typeof(Foo&lt;&gt;)</c>.</param>
    /// <param name="typeArgument">The generic type argument.</param>
    /// <param name="factoryFactory">
    /// Given the closed generic type, produces a parameterless constructor
    /// delegate. Callers can source-generate this for full AOT friendliness,
    /// or hand-write a <c>typeof(...).GetConstructor(...).CreateDelegate(...)</c>
    /// expression. Called at most once per cache key.
    /// </param>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument,
        Func<Type, Func<T>> factoryFactory)
    {
        var factory = (Func<T>)_zeroArg.GetOrAdd(
            (openType, typeArgument),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg)),
            factoryFactory);

        return factory();
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> by closing
    /// <paramref name="openType"/> with <paramref name="typeArgument"/> and
    /// invoking a single-argument constructor with <paramref name="ctorArgument"/>.
    /// </summary>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument,
        object ctorArgument,
        Func<Type, Func<object, T>> factoryFactory)
    {
        var factory = (Func<object, T>)_oneArg.GetOrAdd(
            (openType, typeArgument),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg)),
            factoryFactory);

        return factory(ctorArgument);
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> with two constructor arguments.
    /// </summary>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument1,
        Type typeArgument2,
        object ctorArgument1,
        object ctorArgument2,
        Func<Type, Func<object, object, T>> factoryFactory)
    {
        var factory = (Func<object, object, T>)_twoArg.GetOrAdd(
            (openType, typeArgument1, typeArgument2),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg1, key.Arg2)),
            factoryFactory);

        return factory(ctorArgument1, ctorArgument2);
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> with three constructor arguments.
    /// </summary>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument1,
        Type typeArgument2,
        Type typeArgument3,
        object ctorArgument1,
        object ctorArgument2,
        object ctorArgument3,
        Func<Type, Func<object, object, object, T>> factoryFactory)
    {
        var factory = (Func<object, object, object, T>)_threeArg.GetOrAdd(
            (openType, typeArgument1, typeArgument2, typeArgument3),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg1, key.Arg2, key.Arg3)),
            factoryFactory);

        return factory(ctorArgument1, ctorArgument2, ctorArgument3);
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> by closing
    /// <paramref name="openType"/> with <paramref name="typeArgument"/> and
    /// invoking a two-argument constructor.
    /// </summary>
    /// <remarks>
    /// Companion to the existing <c>(1 type arg, 1 ctor arg)</c> overload.
    /// Added in #223 for hot-path callers (e.g. Marten's
    /// <c>ListQueryHandler&lt;T&gt;(ISqlFragment, ISelector&lt;T&gt;)</c>)
    /// where the original 4 overloads didn't fit.
    /// </remarks>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument,
        object ctorArgument1,
        object ctorArgument2,
        Func<Type, Func<object, object, T>> factoryFactory)
    {
        var factory = (Func<object, object, T>)_t1c2.GetOrAdd(
            (openType, typeArgument),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg)),
            factoryFactory);

        return factory(ctorArgument1, ctorArgument2);
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> by closing
    /// <paramref name="openType"/> with <paramref name="typeArgument"/> and
    /// invoking a three-argument constructor.
    /// </summary>
    /// <remarks>
    /// Companion to the existing <c>(1 type arg, 1 ctor arg)</c> overload.
    /// Added in #223 for hot-path callers (e.g. Marten's
    /// <c>ListIncludePlan&lt;T&gt;</c> / <c>IncludePlan&lt;T&gt;</c>) where
    /// the original 4 overloads didn't fit.
    /// </remarks>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument,
        object ctorArgument1,
        object ctorArgument2,
        object ctorArgument3,
        Func<Type, Func<object, object, object, T>> factoryFactory)
    {
        var factory = (Func<object, object, object, T>)_t1c3.GetOrAdd(
            (openType, typeArgument),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg)),
            factoryFactory);

        return factory(ctorArgument1, ctorArgument2, ctorArgument3);
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> by closing
    /// <paramref name="openType"/> with two type arguments and invoking a
    /// three-argument constructor.
    /// </summary>
    /// <remarks>
    /// Companion to the existing <c>(2 type args, 2 ctor args)</c> overload.
    /// Added in #223 for hot-path callers (e.g. Marten's
    /// <c>DictionaryIncludePlan&lt;T, TKey&gt;</c>) where the original
    /// overload set didn't fit.
    /// </remarks>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument1,
        Type typeArgument2,
        object ctorArgument1,
        object ctorArgument2,
        object ctorArgument3,
        Func<Type, Func<object, object, object, T>> factoryFactory)
    {
        var factory = (Func<object, object, object, T>)_t2c3.GetOrAdd(
            (openType, typeArgument1, typeArgument2),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg1, key.Arg2)),
            factoryFactory);

        return factory(ctorArgument1, ctorArgument2, ctorArgument3);
    }

    /// <summary>
    /// Build an instance of <typeparamref name="T"/> by closing
    /// <paramref name="openType"/> with <paramref name="typeArgument"/> and
    /// invoking a constructor with the supplied <paramref name="ctorArguments"/>
    /// array. Use this overload for high-arity constructors that don't fit the
    /// fixed-arity overloads (e.g. four or more ctor arguments).
    /// </summary>
    /// <remarks>
    /// The cache key is <c>(openType, typeArgument)</c> only — the contents of
    /// <paramref name="ctorArguments"/> are passed to the cached delegate at
    /// invocation time, not captured into the key. Callers pay an array
    /// allocation on every call; reach for one of the fixed-arity overloads
    /// when they fit.
    /// </remarks>
    [RequiresDynamicCode("The default factoryFactory implementation may use MakeGenericType / Activator.CreateInstance; supply an AOT-safe delegate factory to avoid this.")]
    public static T BuildAs<T>(
        Type openType,
        Type typeArgument,
        object[] ctorArguments,
        Func<Type, Func<object[], T>> factoryFactory)
    {
        var factory = (Func<object[], T>)_t1cArray.GetOrAdd(
            (openType, typeArgument),
            static (key, ff) => ff(key.Open.MakeGenericType(key.Arg)),
            factoryFactory);

        return factory(ctorArguments);
    }

    /// <summary>
    /// Test/diagnostic helper. Removes any cached delegate for the given key.
    /// </summary>
    public static void Clear()
    {
        _zeroArg.Clear();
        _oneArg.Clear();
        _twoArg.Clear();
        _threeArg.Clear();
        _t1c2.Clear();
        _t1c3.Clear();
        _t2c3.Clear();
        _t1cArray.Clear();
    }
}
