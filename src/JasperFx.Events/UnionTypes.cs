using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;

namespace JasperFx.Events;

/// <summary>
///     One case of a C# union type, as System.Text.Json describes it.
/// </summary>
/// <param name="CaseType">The CLR type of the case</param>
/// <param name="IsNullable">True when the case was declared nullable, as in <c>union MaybeCat(Cat?, Dog)</c></param>
public sealed record UnionCase(Type CaseType, bool IsNullable);

/// <summary>
///     Metadata and helpers for C# 15 union types (<c>union AccountEvent(Deposited, Withdrawn)</c>, or a
///     hand-written struct marked with <c>[Union]</c> that implements <c>IUnion</c>) used as events.
///     <para>
///     A union is a struct carrying <c>System.Runtime.CompilerServices.UnionAttribute</c> and implementing
///     <c>System.Runtime.CompilerServices.IUnion</c>, whose <c>Value</c> is the active case. Those types only
///     exist from .NET 11, so on earlier targets every member here behaves as if no type is a union:
///     <see cref="IsUnion" /> is false, <see cref="CaseTypes" /> is empty and <see cref="Unwrap" /> hands back
///     what it was given.
///     </para>
///     <para>
///     Events are stored as the active case, never as the union: System.Text.Json writes a union as its bare
///     case with no discriminator, so only the event type name can tell the cases apart when reading back.
///     See spikes/StjUnionSpike (jasperfx#1037) for the measurements behind that.
///     </para>
/// </summary>
/// <remarks>
///     Never test a variable statically typed as a union with <c>is IUnion</c>: C# union patterns test the
///     <em>case</em>, so <c>pet is IUnion</c> is false. Everything here takes <c>object</c> or <c>Type</c>.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Reads the public constructors of a union type to list its cases. Union event types are preserved by event registration on the caller side, like every other event type.")]
[UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
    Justification = "Activator.CreateInstance on a union type whose public case constructors are preserved by event registration on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "JsonSerializerOptions.GetTypeInfo is only reached through UnionCasesFor, whose caller supplies options that can describe the union (a source-generated context under AOT).")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JsonSerializerOptions.GetTypeInfo is only reached through UnionCasesFor, whose caller supplies options that can describe the union (a source-generated context under AOT).")]
public static class UnionTypes
{
    public const string UnionAttributeName = "System.Runtime.CompilerServices.UnionAttribute";

    private static readonly ConcurrentDictionary<Type, Type[]> _caseTypes = new();

    /// <summary>
    ///     Is this type a union: marked with <c>[Union]</c> or implementing <c>IUnion</c>? Always false
    ///     before .NET 11.
    /// </summary>
    public static bool IsUnion(this Type type)
    {
#if NET11_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(type);
        return type.IsDefined(typeof(System.Runtime.CompilerServices.UnionAttribute), false)
               || typeof(System.Runtime.CompilerServices.IUnion).IsAssignableFrom(type);
#else
        return false;
#endif
    }

    /// <summary>
    ///     The case types of a union, read from its public single-parameter constructors (a union has one
    ///     per case). Needs no serializer options, so projection discovery can use it. Empty when
    ///     <paramref name="unionType" /> is not a union.
    /// </summary>
    public static IReadOnlyList<Type> CaseTypes(this Type unionType)
    {
        ArgumentNullException.ThrowIfNull(unionType);
        if (!unionType.IsUnion()) return Array.Empty<Type>();

        return _caseTypes.GetOrAdd(unionType, static t => t
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(c => c.GetParameters())
            .Where(p => p.Length == 1)
            .Select(p => Nullable.GetUnderlyingType(p[0].ParameterType) ?? p[0].ParameterType)
            .Distinct()
            .ToArray());
    }

    /// <summary>
    ///     The event types a handler parameter of type <paramref name="type" /> stands for: the case types,
    ///     each expanded again if it is itself a union, when <paramref name="type" /> is a union, otherwise
    ///     just <paramref name="type" />.
    /// </summary>
    public static IEnumerable<Type> ExpandToCaseTypes(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsUnion())
        {
            yield return type;
            yield break;
        }

        foreach (var caseType in type.CaseTypes())
        {
            foreach (var expanded in caseType.ExpandToCaseTypes())
            {
                yield return expanded;
            }
        }
    }

    /// <summary>
    ///     The cases of a union as System.Text.Json sees them through <paramref name="options" />, including
    ///     whether each case is nullable. Empty when <paramref name="unionType" /> is not a union, or before
    ///     .NET 11. Like <c>JsonSerializerOptions.GetTypeInfo</c>, this makes <paramref name="options" />
    ///     read-only, falling back to the default reflection resolver when none is set.
    /// </summary>
    public static IReadOnlyList<UnionCase> UnionCasesFor(this Type unionType, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(unionType);
        ArgumentNullException.ThrowIfNull(options);

#if NET11_0_OR_GREATER
        if (!unionType.IsUnion()) return Array.Empty<UnionCase>();

        options.MakeReadOnly(true);
        var typeInfo = options.GetTypeInfo(unionType);
        if (typeInfo.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Union)
        {
            return Array.Empty<UnionCase>();
        }

        return typeInfo.UnionCases.Select(x => new UnionCase(x.CaseType, x.IsNullable)).ToArray();
#else
        return Array.Empty<UnionCase>();
#endif
    }

    /// <summary>
    ///     The active case of a boxed union, unwrapping nested unions until a value that is not a union is
    ///     reached. Anything that is not a union is returned unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The union is a default value (<c>default(AccountEvent)</c>) with no active case, so there is no
    ///     event to store.
    /// </exception>
    public static object Unwrap(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

#if NET11_0_OR_GREATER
        while (value is System.Runtime.CompilerServices.IUnion union)
        {
            var unionType = value.GetType();
            value = union.Value ?? throw new InvalidOperationException(
                $"Union '{unionType.FullNameInCode()}' has no active case (it is a default value), so there is no event to append.");
        }
#endif

        return value;
    }

    /// <summary>
    ///     Box <paramref name="caseValue" /> into <paramref name="unionType" /> through the union's public
    ///     constructor for that case. Returns <paramref name="caseValue" /> unchanged when
    ///     <paramref name="unionType" /> is not a union or the value is already of that type.
    /// </summary>
    public static object Wrap(Type unionType, object caseValue)
    {
        ArgumentNullException.ThrowIfNull(unionType);
        ArgumentNullException.ThrowIfNull(caseValue);

        if (!unionType.IsUnion() || unionType.IsInstanceOfType(caseValue)) return caseValue;

        return Activator.CreateInstance(unionType, caseValue)!;
    }
}
