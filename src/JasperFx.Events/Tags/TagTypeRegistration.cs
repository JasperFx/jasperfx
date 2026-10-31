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
public static class TagTypeRegistration
{
    /// <summary>
    /// Create a tag type registration for the given strong-typed identifier type, discovering its
    /// inner primitive through <see cref="ValueTypeInfo"/>.
    /// </summary>
    /// <remarks>
    /// Annotated rather than suppressed, so a trimmed or Native AOT build reports the call site and
    /// points at <see cref="FromUnwrapper{TTag, TInner}"/>: the discovery reflects over the tag type's
    /// members and closes <c>TagTypeRegistration&lt;TTag, TInner&gt;</c> with <c>MakeGenericType</c>,
    /// and either can fail at runtime in such a build.
    /// </remarks>
    [RequiresUnreferencedCode("Discovers the tag type's inner value through ValueTypeInfo, which reflects over its members. Under trimming or Native AOT use TagTypeRegistration.FromUnwrapper<TTag, TInner>.")]
    [RequiresDynamicCode("Closes TagTypeRegistration<TTag, TInner> with MakeGenericType. Under Native AOT use TagTypeRegistration.FromUnwrapper<TTag, TInner>, which closes nothing at runtime.")]
    public static ITagTypeRegistration Create<TTag>(string? tableSuffix = null) where TTag : notnull
    {
        var vti = ValueTypeInfo.ForType(typeof(TTag));
        var closedType = typeof(TagTypeRegistration<,>).MakeGenericType(typeof(TTag), vti.SimpleType);
        return (ITagTypeRegistration)Activator.CreateInstance(closedType, vti, tableSuffix ?? typeof(TTag).Name.ToTableAlias())!;
    }

    /// <summary>
    /// Create a tag type registration from an explicit unwrapper. Nothing is discovered by
    /// reflection, no generic type is closed at runtime and no expression is compiled, so
    /// this is the method to use under Native AOT, where <see cref="Create{TTag}"/> can fail
    /// for lack of native code for TagTypeRegistration&lt;TTag, TInner&gt; or, when trimmed,
    /// of the tag type's members.
    /// </summary>
    /// <typeparam name="TInner">
    /// The primitive the tag table stores. Nothing here checks it, because which primitives a tag
    /// column can hold is the store's decision; an unsupported one fails when the store builds the
    /// tag table, not here. Use the type <see cref="Create{TTag}"/> would have discovered — the type
    /// of the tag's single public value property, typically <see cref="Guid"/>, <see cref="string"/>,
    /// <see cref="int"/> or <see cref="long"/>.
    /// </typeparam>
    /// <param name="unwrapper">Reads the inner primitive out of the tag, e.g. <c>id => id.Value</c>.</param>
    /// <param name="tableSuffix">Defaults to the tag type's name as a table alias, as in <see cref="Create{TTag}"/>.</param>
    /// <remarks>
    /// Deliberately not an overload of <see cref="Create{TTag}"/>: Marten resolves that method with
    /// <c>GetMethod(nameof(Create))</c> to auto-register projection identities, and a second
    /// <c>Create</c> would make the Marten versions that use that lookup throw
    /// <c>AmbiguousMatchException</c> from their event graph's type initializer.
    /// </remarks>
    public static ITagTypeRegistration FromUnwrapper<TTag, TInner>(Func<TTag, TInner> unwrapper, string? tableSuffix = null)
        where TTag : notnull
    {
        return new TagTypeRegistration<TTag, TInner>(unwrapper, tableSuffix ?? typeof(TTag).Name.ToTableAlias());
    }

    /// <summary>
    /// The explicit registration of <paramref name="tagType" /> as a line of code, for an error or log message
    /// to hand back when a tag type could not be registered by inference. The inner type and member follow the
    /// tag when it can be read, with <c>TInner</c> and <c>Value</c> as placeholders when it can't, and a
    /// missing or empty suffix becomes the one <see cref="Create{TTag}"/> uses by default.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Diagnostic text only; if the tag type's members were trimmed the line falls back to placeholders.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Diagnostic text only; if the tag type's members were trimmed the line falls back to placeholders.")]
    public static string RegistrationLine(Type tagType, string? tableSuffix, Type? aggregateType)
    {
        var inner = "TInner";
        var member = "Value";
        try
        {
            var valueType = ValueTypeInfo.ForType(tagType);
            inner = valueType.SimpleType.NameInCode();
            member = valueType.ValueProperty.Name;
        }
        catch (Exception)
        {
            // Not a shape ValueTypeInfo understands, or its members were trimmed; the placeholders still name
            // the overload
        }

        var suffix = string.IsNullOrEmpty(tableSuffix) ? tagType.Name.ToTableAlias() : tableSuffix;
        var forAggregate = aggregateType == null ? "" : $".ForAggregate<{aggregateType.NameInCode()}>()";
        return $"opts.Events.RegisterTagType<{tagType.NameInCode()}, {inner}>(x => x.{member}, \"{suffix}\"){forAggregate};";
    }
}

/// <summary>
/// Generic registration of a strong-typed identifier as an event tag type.
/// TTag is the outer strong-typed identifier (e.g., StudentId),
/// TInner is the wrapped primitive (e.g., string).
/// </summary>
public class TagTypeRegistration<TTag, TInner> : ITagTypeRegistration where TTag : notnull
{
    private readonly Func<TTag, TInner> _unwrapper;

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Inference: compiles the ValueTypeInfo unwrapper via FastExpressionCompiler. The unwrapper constructor below compiles nothing.")]
    public TagTypeRegistration(ValueTypeInfo valueTypeInfo, string tableSuffix)
    {
        TagType = typeof(TTag);
        SimpleType = typeof(TInner);
        TableSuffix = tableSuffix;
        _unwrapper = valueTypeInfo.UnWrapper<TTag, TInner>();
    }

    /// <summary>
    /// Build the registration from an explicit unwrapper instead of a <see cref="ValueTypeInfo"/>.
    /// Compiles nothing and reflects over nothing, so it is safe under Native AOT.
    /// </summary>
    public TagTypeRegistration(Func<TTag, TInner> unwrapper, string tableSuffix)
    {
        ArgumentNullException.ThrowIfNull(unwrapper);
        ArgumentException.ThrowIfNullOrEmpty(tableSuffix);

        TagType = typeof(TTag);
        SimpleType = typeof(TInner);
        TableSuffix = tableSuffix;
        _unwrapper = unwrapper;
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
