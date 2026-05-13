using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Tags;

/// <summary>
/// Static factory for creating <see cref="ITagTypeRegistration"/> instances.
/// Builds the ValueTypeInfo first, validates the type, then creates the
/// properly closed generic TagTypeRegistration&lt;TTag, TInner&gt;.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: ValueTypeInfo.ForType + Type.MakeGenericType(TagTypeRegistration<,>) for strong-typed-id discovery. Tag types preserved at registration on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument TTag flows into ValueTypeInfo.ForType / MakeGenericType. Preserved by registration.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: Type.MakeGenericType(TagTypeRegistration<,>) — runtime code generation. AOT consumers should register concrete tag types per the AOT publishing guide.")]
public static class TagTypeRegistration
{
    /// <summary>
    /// Create a tag type registration for the given strong-typed identifier type.
    /// </summary>
    public static ITagTypeRegistration Create<TTag>(string? tableSuffix = null) where TTag : notnull
    {
        var vti = ValueTypeInfo.ForType(typeof(TTag));
        var closedType = typeof(TagTypeRegistration<,>).MakeGenericType(typeof(TTag), vti.SimpleType);
        return (ITagTypeRegistration)Activator.CreateInstance(closedType, vti, tableSuffix ?? typeof(TTag).Name.ToTableAlias())!;
    }
}

/// <summary>
/// Generic registration of a strong-typed identifier as an event tag type.
/// TTag is the outer strong-typed identifier (e.g., StudentId),
/// TInner is the wrapped primitive (e.g., string).
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: holds ValueTypeInfo wrapper/unwrapper delegates compiled via FastExpressionCompiler. Tag types preserved at registration on the caller side.")]
public class TagTypeRegistration<TTag, TInner> : ITagTypeRegistration where TTag : notnull
{
    private readonly Func<TTag, TInner> _unwrapper;

    public TagTypeRegistration(ValueTypeInfo valueTypeInfo, string tableSuffix)
    {
        TagType = typeof(TTag);
        SimpleType = typeof(TInner);
        TableSuffix = tableSuffix;
        _unwrapper = valueTypeInfo.UnWrapper<TTag, TInner>();
    }

    /// <inheritdoc />
    public Type TagType { get; }

    /// <inheritdoc />
    public Type SimpleType { get; }

    /// <inheritdoc />
    public string TableSuffix { get; }

    /// <inheritdoc />
    public Type? AggregateType { get; set; }

    /// <inheritdoc />
    public object ExtractValue(object tagInstance)
    {
        return _unwrapper((TTag)tagInstance)!;
    }
    /// <inheritdoc />
    public ITagTypeRegistration ForAggregate<T>()
    {
        AggregateType = typeof(T);
        return this;
    }

    /// <inheritdoc />
    public ITagTypeRegistration ForAggregate(Type aggregateType)
    {
        AggregateType = aggregateType;
        return this;
    }
}
