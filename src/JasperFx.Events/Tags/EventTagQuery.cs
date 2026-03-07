namespace JasperFx.Events.Tags;

/// <summary>
/// Represents a single condition in a DCB tag query.
/// </summary>
public record EventTagQueryCondition(Type? EventType, Type TagType, object TagValue);

/// <summary>
/// A specification for querying events by their tags, following the
/// Dynamic Consistency Boundary (DCB) pattern. Conditions are OR'd together.
/// </summary>
public class EventTagQuery
{
    private readonly List<EventTagQueryCondition> _conditions = new();

    /// <summary>
    /// The conditions in this query, combined with OR logic.
    /// </summary>
    public IReadOnlyList<EventTagQueryCondition> Conditions => _conditions;

    /// <summary>
    /// Add a condition: events of type TEvent tagged with the given tag value.
    /// </summary>
    public EventTagQuery Or<TEvent, TTag>(TTag tagValue) where TTag : notnull
    {
        _conditions.Add(new EventTagQueryCondition(typeof(TEvent), typeof(TTag), tagValue));
        return this;
    }

    /// <summary>
    /// Add a condition: any event tagged with the given tag value (no event type filter).
    /// </summary>
    public EventTagQuery Or<TTag>(TTag tagValue) where TTag : notnull
    {
        _conditions.Add(new EventTagQueryCondition(null, typeof(TTag), tagValue));
        return this;
    }

    /// <summary>
    /// Get the distinct tag types referenced by this query.
    /// </summary>
    public IReadOnlyList<Type> TagTypes => _conditions.Select(c => c.TagType).Distinct().ToList();
}
