using System.Linq.Expressions;
using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Declares the fields of one modelled type (jasperfx#1299). Opened with <see cref="EventModelBuilder.Shape{T}"/>;
/// each <see cref="Field"/> opens a <see cref="FieldBuilder"/> that reads as a sentence and returns here through
/// its own <see cref="FieldBuilder.Field"/>.
/// </summary>
/// <remarks>
/// Everything declared here sits on the <see cref="EventModelProvenance.Declared"/> rung. Once the type exists,
/// the fields the source generator or reflection derive from it win, and a declared field the code lacks is a
/// model-level <see cref="HotspotOrigin.SourceDisagreement"/> on <see cref="EventModelRole.Fields"/>. A field's
/// <c>From</c> mapping, tag and description are declared-only detail and travel onto the derived field.
/// </remarks>
public sealed class TypeShapeBuilder
{
    private readonly List<FieldBuilder> _fields = new();

    internal TypeShapeBuilder(TypeDescriptor type)
    {
        Type = type;
    }

    /// <summary>The type whose fields this declares.</summary>
    public TypeDescriptor Type { get; }

    /// <summary>
    /// Declare a field. Its type defaults to <see cref="string"/> until <see cref="FieldBuilder.OfType{T}"/> says
    /// otherwise. Declaring a field twice returns the same builder.
    /// </summary>
    /// <param name="name">The field's name.</param>
    public FieldBuilder Field(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A field needs a name", nameof(name));
        }

        name = name.Trim();
        var existing = _fields.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var field = new FieldBuilder(this, name);
        _fields.Add(field);
        return field;
    }

    /// <summary>Snapshot the shape as a descriptor.</summary>
    public TypeShapeDescriptor Build()
        => new(Type, _fields.Select(x => x.Build()).ToList());
}

/// <summary>
/// Declares one field of a <see cref="TypeShapeBuilder"/> (jasperfx#1299): its type, cardinality, flags, tag and
/// where its value comes from.
/// </summary>
public sealed class FieldBuilder
{
    private readonly TypeShapeBuilder _shape;
    private TypeDescriptor _type = TypeDescriptor.For(typeof(string));
    private FieldCardinality _cardinality = FieldCardinality.One;
    private bool _optional;
    private bool _identity;
    private string? _tag;
    private bool _generated;
    private bool _pii;
    private FieldMappingDescriptor? _mapping;
    private string? _description;

    internal FieldBuilder(TypeShapeBuilder shape, string name)
    {
        _shape = shape;
        Name = name;
    }

    /// <summary>The field's name.</summary>
    public string Name { get; }

    /// <summary>The field's type — the element type for a collection, which <see cref="Many"/> says.</summary>
    public FieldBuilder OfType<T>()
    {
        _type = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>The field's type, by name, before it exists: a reference to another modelled type.</summary>
    public FieldBuilder OfType(string typeName)
    {
        _type = EventModelSliceBuilder.Declared(typeName);
        return this;
    }

    /// <summary>The field holds a collection of its type.</summary>
    public FieldBuilder Many()
    {
        _cardinality = FieldCardinality.Many;
        return this;
    }

    /// <summary>The field may be absent or null.</summary>
    public FieldBuilder Optional()
    {
        _optional = true;
        return this;
    }

    /// <summary>The field identifies the thing: its stream id, a strong-typed id.</summary>
    public FieldBuilder Identity()
    {
        _identity = true;
        return this;
    }

    /// <summary>The field's value is the consistency tag <typeparamref name="TTag"/> (jasperfx#1297).</summary>
    public FieldBuilder Tagged<TTag>() => Tagged(EventTagDescriptor.NameFor(typeof(TTag)));

    /// <summary>The field's value is the consistency tag named <paramref name="tag"/>.</summary>
    public FieldBuilder Tagged(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("A tag needs a name", nameof(tag));
        _tag = tag.Trim();
        return this;
    }

    /// <summary>
    /// The system generates the value — an id minted on the way in, a timestamp — so no command or event has to
    /// supply it, and the information-completeness check does not ask where it comes from.
    /// </summary>
    public FieldBuilder Generated()
    {
        _generated = true;
        return this;
    }

    /// <summary>The value is personally identifiable information.</summary>
    public FieldBuilder Pii()
    {
        _pii = true;
        return this;
    }

    /// <summary>
    /// Where the value comes from, when the names differ: the member of <typeparamref name="TSource"/> that
    /// <paramref name="member"/> names — <c>Field("Total").From&lt;OrderPlaced&gt;(x =&gt; x.OrderTotal)</c>. The board's
    /// <c>mapping</c>, and what the information-completeness check follows.
    /// </summary>
    public FieldBuilder From<TSource>(Expression<Func<TSource, object?>> member)
        => From(TypeDescriptor.For(typeof(TSource)), EventModelSliceBuilder.MemberNameOf(member));

    /// <summary>Where the value comes from, by the source's member name: <c>From&lt;OrderPlaced&gt;("OrderTotal")</c>.</summary>
    public FieldBuilder From<TSource>(string sourceField) => From(TypeDescriptor.For(typeof(TSource)), sourceField);

    /// <summary>Where the value comes from, before the source type exists: <c>From("OrderPlaced", "OrderTotal")</c>.</summary>
    public FieldBuilder From(string sourceTypeName, string sourceField)
        => From(EventModelSliceBuilder.Declared(sourceTypeName), sourceField);

    private FieldBuilder From(TypeDescriptor source, string sourceField)
    {
        if (string.IsNullOrWhiteSpace(sourceField))
        {
            throw new ArgumentException("A mapping needs the source field's name", nameof(sourceField));
        }

        _mapping = new FieldMappingDescriptor(source, sourceField.Trim());
        return this;
    }

    /// <summary>Describe the field in plain text or markdown (jasperfx#1298).</summary>
    public FieldBuilder Describe(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A description needs text", nameof(text));
        _description = text.Trim();
        return this;
    }

    /// <summary>Open the next field of the same shape.</summary>
    public FieldBuilder Field(string name) => _shape.Field(name);

    /// <summary>The shape this field belongs to, to continue declaring the model.</summary>
    public TypeShapeBuilder Shape => _shape;

    internal FieldDescriptor Build()
        => new(Name, _type)
        {
            Cardinality = _cardinality,
            Optional = _optional,
            Identity = _identity,
            Tag = _tag,
            Generated = _generated,
            Pii = _pii,
            Mapping = _mapping,
            Description = _description,
        };
}
