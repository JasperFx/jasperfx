using System.Text.Json;
using JasperFx.Descriptors;

namespace JasperFx.Events.Tags;

/// <summary>
/// Wire-safe representation of a single <see cref="EventTagQueryCondition"/>: the
/// tag type and event type are carried as <see cref="TypeDescriptor"/> name strings
/// and the tag value as JSON, so the condition can cross a message hop without
/// shipping CLR types.
/// </summary>
/// <param name="EventType">Descriptor of the event type filter, or <see langword="null"/> for "any event carrying this tag".</param>
/// <param name="TagType">Descriptor of the strong-typed tag identity type.</param>
/// <param name="TagValue">The tag value serialized as JSON, deserialized back against <paramref name="TagType"/> on the service side.</param>
public sealed record EventTagQueryConditionSpec(
    TypeDescriptor? EventType,
    TypeDescriptor TagType,
    JsonElement TagValue);

/// <summary>
/// Serializable, round-trippable form of an <see cref="EventTagQuery"/> — the full
/// rich query (OR conditions plus <c>AndEventsOfType</c> event-type filters), not the
/// lossy AND-only <c>IReadOnlyDictionary&lt;string,string&gt;</c> form. Lets a DCB-sourced
/// projection replay / <c>AggregateToMany</c> carry a real Dynamic Consistency Boundary
/// query over the wire and have it resolved back to CLR types on the service side against
/// the registered tag/event graph. See jasperfx#545.
/// </summary>
/// <param name="Conditions">The OR'd conditions, mirroring <see cref="EventTagQuery.Conditions"/>.</param>
public sealed record EventTagQuerySpec(IReadOnlyList<EventTagQueryConditionSpec> Conditions)
{
    /// <summary>
    /// Flatten a live <see cref="EventTagQuery"/> into its wire form. Each condition's
    /// tag value is serialized using its runtime type so a strong-typed id round-trips.
    /// </summary>
    /// <param name="query">The query to serialize.</param>
    /// <param name="options">Optional serializer options; the same options must be supplied to <see cref="Resolve"/>.</param>
    public static EventTagQuerySpec From(EventTagQuery query, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var conditions = query.Conditions
            .Select(c => new EventTagQueryConditionSpec(
                c.EventType is null ? null : TypeDescriptor.For(c.EventType),
                TypeDescriptor.For(c.TagType),
                // Serialize under the value's *runtime* type; the compile-time type here is
                // object, and object-typed serialization would emit an empty document.
                JsonSerializer.SerializeToElement(c.TagValue, c.TagValue.GetType(), options)))
            .ToList();

        return new EventTagQuerySpec(conditions);
    }

    /// <summary>
    /// Rehydrate the spec into a live <see cref="EventTagQuery"/>, resolving each tag/event
    /// type name back to a CLR <see cref="Type"/> via <paramref name="resolveType"/> and
    /// deserializing each tag value against its resolved tag type. The resolver is expected
    /// to consult the store's registered tag/event graph.
    /// </summary>
    /// <param name="resolveType">Resolves a <see cref="TypeDescriptor"/> to its CLR type; see <see cref="ResolverFor"/> for a graph-backed default.</param>
    /// <param name="options">Optional serializer options; must match those used by <see cref="From"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolveType"/> is null.</exception>
    /// <exception cref="UnknownTagQueryTypeException">A tag or event type name cannot be resolved against the registered graph.</exception>
    public EventTagQuery Resolve(Func<TypeDescriptor, Type?> resolveType, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolveType);

        var conditions = new List<EventTagQueryCondition>(Conditions.Count);
        foreach (var spec in Conditions)
        {
            var tagType = resolveType(spec.TagType)
                          ?? throw new UnknownTagQueryTypeException(spec.TagType);

            Type? eventType = null;
            if (spec.EventType is not null)
            {
                eventType = resolveType(spec.EventType)
                            ?? throw new UnknownTagQueryTypeException(spec.EventType);
            }

            var tagValue = spec.TagValue.Deserialize(tagType, options)
                           ?? throw new InvalidOperationException(
                               $"Tag value for tag type '{spec.TagType.FullName}' deserialized to null.");

            conditions.Add(new EventTagQueryCondition(eventType, tagType, tagValue));
        }

        return EventTagQuery.FromConditions(conditions);
    }

    /// <summary>
    /// Build a <see cref="TypeDescriptor"/>-to-<see cref="Type"/> resolver over a known set of
    /// types — the store's registered tag types and event types. Matches on full name (falling
    /// back to simple name when full names are ambiguous only across differing assemblies is not
    /// a concern for the registered graph). Returns <see langword="null"/> for an unknown type so
    /// <see cref="Resolve"/> can raise a precise error.
    /// </summary>
    /// <param name="knownTypes">The registered tag/event graph types the query may reference.</param>
    public static Func<TypeDescriptor, Type?> ResolverFor(IEnumerable<Type> knownTypes)
    {
        ArgumentNullException.ThrowIfNull(knownTypes);

        var byFullName = new Dictionary<string, Type>();
        foreach (var type in knownTypes)
        {
            if (type.FullName is { } fullName)
            {
                byFullName[fullName] = type;
            }
        }

        return descriptor => descriptor.FullName is { } fullName && byFullName.TryGetValue(fullName, out var type)
            ? type
            : null;
    }
}

/// <summary>
/// Raised when an <see cref="EventTagQuerySpec"/> references a tag or event type that
/// cannot be resolved against the registered tag/event graph on the service side.
/// </summary>
public sealed class UnknownTagQueryTypeException(TypeDescriptor descriptor)
    : Exception($"Unable to resolve tag query type '{descriptor.FullName}' against the registered tag/event graph. " +
                "Ensure the type is registered on the service side before replaying a DCB-sourced query.")
{
    /// <summary>The descriptor that could not be resolved.</summary>
    public TypeDescriptor Descriptor { get; } = descriptor;
}
