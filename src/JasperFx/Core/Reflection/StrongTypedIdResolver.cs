using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using FastExpressionCompiler;

namespace JasperFx.Core.Reflection;

/// <summary>
///     Which shapes <see cref="StrongTypedIdResolver" /> accepts as a wrapper, beyond the single value it
///     has to carry.
/// </summary>
public enum StrongTypedIdStyle
{
    /// <summary>
    ///     A struct only. The shape every id generator emits, and what Polecat requires of an identity.
    /// </summary>
    ValueType,

    /// <summary>
    ///     A struct, or a class built through a public static factory rather than a public constructor.
    ///     Vogen's <c>[ValueObject&lt;int&gt;] partial record Age</c> qualifies; an ordinary
    ///     <c>record Address(string City)</c> does not, and stays a nested object. What a store asks for
    ///     a member that is not the identity.
    /// </summary>
    ValueObject,

    /// <summary>
    ///     A struct or any class, Fisher's rule: its tag types are routinely a plain
    ///     <c>record ShipmentId(Guid Value)</c>. Only safe to ask of a type already known to be an
    ///     identity, because a single-property DTO answers yes.
    /// </summary>
    AnyType
}

/// <summary>
///     Recognises a strong-typed identifier: a wrapper standing in for one of the four identity types a
///     document store can persist, in the shape <see cref="ValueTypeInfo.ForType" /> describes.
/// </summary>
/// <remarks>
///     <para>
///         GH-1054. Lifted from Polecat's <c>Storage.ValueTypes</c> and Fisher's
///         <c>Storage.StrongTypedId</c>, which discover wrappers rather than asking for them to be
///         registered, and had drifted. Marten registers its value types and has no copy of this.
///     </para>
///     <para>
///         <see cref="ValueTypeInfo.ForType" /> <em>throws</em> for a type that is not a wrapper and caches
///         only its successes. A store asks about every candidate identity and every LINQ member, so the
///         answer here is cached including the negative one: without that, resolving an ordinary
///         <see cref="DateTimeOffset" /> member would raise and swallow an exception each time.
///     </para>
///     <para>
///         This is the half of the Fisher forks that needs nothing from a database. Fisher's
///         <c>StrongTypedIdentification</c> and <c>SqliteGuidIdentification</c> themselves implement
///         Weasel's <c>IIdentification</c>, and what is left in them is how a value crosses the ADO.NET
///         boundary, so they go when Weasel has a raw-value converter, not here.
///     </para>
///     <para>
///         <c>StrongTypedIdentitySource</c> in JasperFx.Events (GH-950) wraps a stream's identity, which
///         is a different job: it already knows the id is a wrapper, and composes
///         <see cref="ValueTypeInfo.CreateWrapper{TOuter,TInner}" /> directly. This answers the question
///         before that one.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: ValueTypeInfo.ForType reflects over the wrapper's public members. A store's wrappers are declared member types of registered documents, which a source-generated JsonSerializerContext keeps. One that was trimmed anyway resolves as 'not a wrapper', which is a refusal rather than a crash.")]
[UnconditionalSuppressMessage("Trimming", "IL2067:UnrecognizedReflectionPattern",
    Justification = "See above.")]
public static class StrongTypedIdResolver
{
    // Negative results are cached too, which is the point. See the remarks.
    private static readonly ConcurrentDictionary<(Type Type, StrongTypedIdStyle Style), ValueTypeInfo?> Resolved = new();

    private static readonly ConcurrentDictionary<Type, Func<object, object>> Wrappers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object>> Unwrappers = new();

    /// <summary>
    ///     The identity types a wrapper may wrap, which are also the identity types a store takes raw.
    /// </summary>
    public static IReadOnlySet<Type> SupportedInnerTypes { get; } =
        new HashSet<Type> { typeof(Guid), typeof(string), typeof(int), typeof(long) };

    /// <summary>
    ///     Whether <paramref name="type" /> is a strong-typed identifier, and its shape if so.
    ///     <c>OrderId?</c> resolves the same as <c>OrderId</c>.
    /// </summary>
    public static bool TryResolve(Type type, [NotNullWhen(true)] out ValueTypeInfo? info,
        StrongTypedIdStyle style = StrongTypedIdStyle.ValueType)
    {
        ArgumentNullException.ThrowIfNull(type);

        info = Resolved.GetOrAdd((type, style), static key => Resolve(key.Type, key.Style));
        return info is not null;
    }

    /// <summary>
    ///     Resolve a wrapper eagerly, throwing when the type cannot be one. What a store's
    ///     <c>RegisterValueType</c> is.
    /// </summary>
    /// <remarks>
    ///     The difference from <see cref="TryResolve" /> is the value of the call. Discovery treats "not
    ///     a wrapper" as the ordinary answer; a caller who has <em>named</em> a type is asserting it is
    ///     one, so here the same answer is a configuration error, heard at startup and not as "has no
    ///     identity member" from somewhere that cannot mention the wrapper.
    /// </remarks>
    /// <exception cref="InvalidValueTypeException"><paramref name="type" /> is not a usable wrapper.</exception>
    public static ValueTypeInfo Register(Type type, StrongTypedIdStyle style = StrongTypedIdStyle.ValueType)
    {
        if (TryResolve(type, out var info, style))
        {
            return info;
        }

        // Two different failures, and telling them apart is worth the second resolve: a shape problem is
        // answered by ValueTypeInfo's own message, while a good wrapper around the wrong inner type has
        // to be told which four are stored.
        var candidate = Nullable.GetUnderlyingType(type) ?? type;

        ValueTypeInfo resolved;
        try
        {
            resolved = ValueTypeInfo.ForType(candidate);
        }
        catch (InvalidValueTypeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new InvalidValueTypeException(type, e.Message);
        }

        throw new InvalidValueTypeException(type, SupportedInnerTypes.Contains(resolved.SimpleType)
            ? $"Its shape is not accepted as a {style} wrapper."
            : $"It wraps a {resolved.SimpleType.FullNameInCode()}, and a store holds an identity as a Guid, string, int or long.");
    }

    /// <summary>
    ///     Build a wrapper around a value already converted to its inner type, for a caller that knows
    ///     the wrapper type only at runtime.
    /// </summary>
    /// <remarks>
    ///     A LINQ projection reads the stored inner value and has only a <see cref="Type" /> to aim at, so
    ///     the generic <see cref="ValueTypeInfo.CreateWrapper{TOuter,TInner}" /> is out of reach: closing
    ///     it with <c>MakeGenericMethod</c> needs native code ILC never generated. The constructor or
    ///     builder is invoked through a delegate cached per wrapper rather than resolved per row.
    /// </remarks>
    public static object Wrap(ValueTypeInfo info, object inner)
        => Wrappers.GetOrAdd(info.OuterType, static (_, i) => BuildWrapper(i), info)(inner);

    /// <summary>
    ///     The inner value of a wrapper, for a caller that knows the wrapper type only at runtime. See
    ///     <see cref="Wrap" />.
    /// </summary>
    public static object Unwrap(ValueTypeInfo info, object wrapper)
        => Unwrappers.GetOrAdd(info.OuterType, static (_, i) => BuildUnwrapper(i), info)(wrapper);

    /// <summary>
    ///     The type a store holds for an identity: a wrapper's inner type, or the type itself.
    /// </summary>
    public static Type StoredTypeFor(Type idType, StrongTypedIdStyle style = StrongTypedIdStyle.ValueType)
    {
        var underlying = Nullable.GetUnderlyingType(idType) ?? idType;
        return TryResolve(underlying, out var info, style) ? info.SimpleType : underlying;
    }

    /// <summary>
    ///     Whether a store can use this type as an identity at all: one of
    ///     <see cref="SupportedInnerTypes" />, or a wrapper around one. The predicate to hand
    ///     <see cref="DocumentIdentity" />, whose default accepts only the raw types.
    /// </summary>
    public static bool IsSupportedIdType(Type type, StrongTypedIdStyle style = StrongTypedIdStyle.ValueType)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return SupportedInnerTypes.Contains(underlying) || TryResolve(underlying, out _, style);
    }

    private static ValueTypeInfo? Resolve(Type type, StrongTypedIdStyle style)
    {
        var candidate = Nullable.GetUnderlyingType(type) ?? type;

        // Cheap exclusions first, so the common case never reaches the throwing ForType.
        if (SupportedInnerTypes.Contains(candidate)
            || candidate.IsPrimitive
            || candidate.IsEnum
            || candidate.IsArray
            || isFrameworkType(candidate))
        {
            return null;
        }

        if (!candidate.IsValueType && style == StrongTypedIdStyle.ValueType)
        {
            return null;
        }

        try
        {
            var resolved = ValueTypeInfo.ForType(candidate);

            if (!SupportedInnerTypes.Contains(resolved.SimpleType))
            {
                return null;
            }

            return !candidate.IsValueType && style == StrongTypedIdStyle.ValueObject && resolved.Ctor != null
                ? null
                : resolved;
        }
        catch (Exception)
        {
            // ForType throws InvalidValueTypeException for anything that is not a single-value wrapper,
            // which is the ordinary answer for most types and not an error.
            return null;
        }
    }

    // Not StartsWith("System"), which is what Fisher wrote and which also excludes every wrapper in a
    // namespace such as SystemsIntegration.Orders. Silently, too: the type just reads as having no id.
    private static bool isFrameworkType(Type type)
        => type.Namespace is { } name
           && (name == "System" || name.StartsWith("System.", StringComparison.Ordinal));

    private static Func<object, object> BuildWrapper(ValueTypeInfo info)
        => RuntimeFeature.IsDynamicCodeSupported ? CompiledWrapper(info) : ReflectiveWrapper(info);

    private static Func<object, object> BuildUnwrapper(ValueTypeInfo info)
        => RuntimeFeature.IsDynamicCodeSupported ? CompiledUnwrapper(info) : ReflectiveUnwrapper(info);

    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private static Func<object, object> CompiledWrapper(ValueTypeInfo info)
    {
        var parameter = Expression.Parameter(typeof(object), "inner");
        var typed = Expression.Convert(parameter, info.SimpleType);

        Expression body = info.Ctor is not null
            ? Expression.New(info.Ctor, typed)
            : Expression.Call(info.Builder!, typed);

        return Expression.Lambda<Func<object, object>>(Expression.Convert(body, typeof(object)), parameter)
            .CompileFast();
    }

    [RequiresDynamicCode("Compiles an expression tree via FastExpressionCompiler.")]
    private static Func<object, object> CompiledUnwrapper(ValueTypeInfo info)
    {
        var parameter = Expression.Parameter(typeof(object), "wrapper");
        var typed = Expression.Convert(parameter, info.OuterType);

        return Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Call(typed, info.ValueProperty.GetMethod!), typeof(object)), parameter)
            .CompileFast();
    }

    // The same members ValueTypeInfo's own Native AOT fallback uses (GH-942), which is what Polecat's
    // DocumentMapping composes by hand today.
    internal static Func<object, object> ReflectiveWrapper(ValueTypeInfo info)
        => info.Ctor is { } ctor
            ? inner => ctor.Invoke([inner])
            : inner => info.Builder!.Invoke(null, [inner])!;

    internal static Func<object, object> ReflectiveUnwrapper(ValueTypeInfo info)
        => wrapper => info.ValueProperty.GetValue(wrapper)!;
}
