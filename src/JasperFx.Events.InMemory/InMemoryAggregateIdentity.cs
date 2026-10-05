using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory;

/// <summary>
/// How an aggregate's identity relates to its stream on the in-memory prototyping store (jasperfx#964).
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Prototyping store; not AOT-compatible.")]
internal static class InMemoryAggregateIdentity
{
    private static readonly ConcurrentDictionary<Type, MemberInfo?> IdMembers = new();

    internal static MemberInfo? FindIdMember(Type aggregateType)
        => IdMembers.GetOrAdd(aggregateType,
            static type => DocumentIdentity.FindIdMember(type, isIdentityType));

    private static bool isIdentityType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (DocumentIdentity.ValidIdTypes.Contains(type)) return true;
        return tryValueType(type, out _);
    }

    /// <summary>
    /// The aggregate's own identity type -- the type its single-stream projection has to be closed over
    /// for the source-generated evolver to match. An aggregate with no identity member at all (a bare
    /// stub, <c>public record Appointment;</c>) takes the stream's identity type, so design-first stubs
    /// can be folded before they grow an <c>Id</c>.
    /// </summary>
    internal static Type ResolveIdType(Type aggregateType, StreamIdentity streamIdentity)
    {
        var member = FindIdMember(aggregateType);
        if (member is null) return streamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);

        var idType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        return Nullable.GetUnderlyingType(idType) ?? idType;
    }

    internal static ProjectionBase CreateLiveProjection<TDoc>(Type idType)
    {
#pragma warning disable CS8714 // TDoc is unconstrained by the factory contract; the projection requires notnull
        ProjectionBase projection =
            idType == typeof(Guid) ? new SingleStreamProjection<TDoc, Guid>()
            : idType == typeof(string) ? new SingleStreamProjection<TDoc, string>()
            : idType == typeof(int) ? new SingleStreamProjection<TDoc, int>()
            : idType == typeof(long) ? new SingleStreamProjection<TDoc, long>()
            : typeof(SingleStreamProjection<,>).CloseAndBuildAs<ProjectionBase>(typeof(TDoc), idType);
#pragma warning restore CS8714

        projection.Lifecycle = ProjectionLifecycle.Live;
        return projection;
    }

    /// <summary>Set the stream's id onto the aggregate's identity member, when it has a writable one.</summary>
    internal static void TrySetIdentity(object aggregate, object streamId)
    {
        switch (FindIdMember(aggregate.GetType()))
        {
            case PropertyInfo { CanWrite: true } property when identityValue(property.PropertyType, streamId) is { } value:
                property.SetValue(aggregate, value);
                break;
            case FieldInfo { IsInitOnly: false } field when identityValue(field.FieldType, streamId) is { } value:
                field.SetValue(aggregate, value);
                break;
        }
    }

    /// <summary>
    /// The Guid or string stream id inside <paramref name="id"/>: the id itself, or the single value of a
    /// strong-typed identity wrapper.
    /// </summary>
    internal static bool TryUnwrap(object id, out object streamId)
    {
        if (id is Guid or string)
        {
            streamId = id;
            return true;
        }

        if (tryValueType(id.GetType(), out var valueType)
            && valueType.ValueProperty.GetValue(id) is { } inner and (Guid or string))
        {
            streamId = inner;
            return true;
        }

        streamId = default!;
        return false;
    }

    private static object? identityValue(Type memberType, object streamId)
    {
        if (memberType.IsInstanceOfType(streamId)) return streamId;

        var target = Nullable.GetUnderlyingType(memberType) ?? memberType;
        if (!tryValueType(target, out var valueType) || valueType.SimpleType != streamId.GetType()) return null;

        return valueType.Ctor is not null
            ? valueType.Ctor.Invoke([streamId])
            : valueType.Builder!.Invoke(null, [streamId]);
    }

    private static bool tryValueType(Type type, out ValueTypeInfo valueType)
    {
        valueType = default!;
        if (type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type.IsEnum) return false;

        try
        {
            valueType = ValueTypeInfo.ForType(type);
            return valueType.SimpleType == typeof(Guid) || valueType.SimpleType == typeof(string)
                   || valueType.SimpleType == typeof(int) || valueType.SimpleType == typeof(long);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
