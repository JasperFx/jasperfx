using System.Reflection;
using JasperFx.Core.Reflection;

namespace JasperFx;

/// <summary>
/// Side-effect-free document ID-resolution convention shared by the Critter Stack stores.
/// Resolves the identity member of a document type via the same attribute-then-convention
/// intent both stores used: <see cref="IdentityAttribute"/> first, then a case-insensitive
/// "Id" member.
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>DocumentMapping.FindIdMember</c> and Polecat's
/// <c>DocumentMapping.FindIdProperty</c>. The shared helper returns <see cref="MemberInfo"/>
/// (Marten supports fields; Polecat was property-only — fields are a strict superset and
/// harmless) and is deliberately free of provider side effects: it does <b>not</b> register
/// Postgres provider mappings or run strong-typed-id detection. Each store keeps that
/// provider-coupled value-type registration on its own side and can supply its richer
/// candidate-type predicate via the <see cref="FindIdMember(Type, Func{Type, bool})"/>
/// overload to preserve its exact resolution behavior. Part of the Critter Stack 2026 dedupe
/// pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>) — see
/// <see href="https://github.com/JasperFx/jasperfx/issues/335">#335</see>.
/// </remarks>
public static class DocumentIdentity
{
    /// <summary>
    /// The canonical scalar identity types both stores accept: <see cref="int"/>,
    /// <see cref="Guid"/>, <see cref="long"/>, <see cref="string"/>. Stores that also accept
    /// strong-typed ids layer that on top via their own predicate — this set is the
    /// provider-agnostic core.
    /// </summary>
    public static readonly Type[] ValidIdTypes = [typeof(int), typeof(Guid), typeof(long), typeof(string)];

    /// <summary>
    /// Resolve the identity member of <paramref name="documentType"/> using the canonical
    /// <see cref="ValidIdTypes"/> as the candidate-type filter. Side-effect-free.
    /// </summary>
    /// <returns>The identity member, or <c>null</c> if none is found.</returns>
    public static MemberInfo? FindIdMember(Type documentType)
        => FindIdMember(documentType, t => ValidIdTypes.Contains(t));

    /// <summary>
    /// Resolve the identity member of <paramref name="documentType"/>, using the supplied
    /// <paramref name="isValidIdType"/> predicate to decide which member types are eligible.
    /// Stores pass their own predicate (which may recognize strong-typed ids) to preserve
    /// their exact resolution behavior while keeping this traversal side-effect-free.
    /// </summary>
    /// <remarks>
    /// Resolution order (Marten's superset): <see cref="IdentityAttribute"/> on a property,
    /// then on a field, then a case-insensitive "id" property, then an "id" field.
    /// </remarks>
    /// <returns>The identity member, or <c>null</c> if none is found.</returns>
    public static MemberInfo? FindIdMember(Type documentType, Func<Type, bool> isValidIdType)
    {
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(isValidIdType);

        var candidateProperties = GetProperties(documentType)
            .Where(p => isValidIdType(p.PropertyType))
            .ToArray();

        var candidateFields = documentType.GetFields()
            .Where(f => isValidIdType(f.FieldType))
            .ToArray();

        return candidateProperties.FirstOrDefault(x => x.HasAttribute<IdentityAttribute>())
               ?? candidateFields.FirstOrDefault(x => x.HasAttribute<IdentityAttribute>())
               ?? (MemberInfo?)candidateProperties.FirstOrDefault(x => x.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
               ?? candidateFields.FirstOrDefault(x => x.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
    }

    private static PropertyInfo[] GetProperties(Type type)
    {
        return type.GetTypeInfo().IsInterface
            ? new[] { type }
                .Concat(type.GetInterfaces())
                .SelectMany(i => i.GetProperties())
                .ToArray()
            : type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderByDescending(x => x.DeclaringType == type)
                .ToArray();
    }
}
