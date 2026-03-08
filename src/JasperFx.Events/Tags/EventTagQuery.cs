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

    // Tracks the last tag value/type added via For() or Or() for AndEventsOfType chaining
    private object? _currentTagValue;
    private Type? _currentTagType;

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
        _currentTagValue = tagValue;
        _currentTagType = typeof(TTag);
        return this;
    }

    /// <summary>
    /// Set the current tag context for subsequent AndEventsOfType calls.
    /// Use this to switch to a different tag value mid-chain.
    /// </summary>
    public EventTagQuery Or<TTag>(TTag tagValue) where TTag : notnull
    {
        _currentTagValue = tagValue;
        _currentTagType = typeof(TTag);
        return this;
    }

    /// <summary>
    /// Get the distinct tag types referenced by this query.
    /// </summary>
    public IReadOnlyList<Type> TagTypes => _conditions.Select(c => c.TagType).Distinct().ToList();

    /// <summary>
    /// Start building a query for a specific tag value. Use AndEventsOfType to filter by event types.
    /// </summary>
    public static EventTagQuery For<TTag>(TTag tagValue) where TTag : notnull
    {
        var query = new EventTagQuery();
        query._currentTagValue = tagValue;
        query._currentTagType = typeof(TTag);
        return query;
    }

    /// <summary>
    /// Add event type conditions for the current tag. Each type becomes a separate Or condition
    /// with the current tag value.
    /// </summary>
    public EventTagQuery AndEventsOfType<T1>()
    {
        EnsureCurrentTag();
        _conditions.Add(new EventTagQueryCondition(typeof(T1), _currentTagType!, _currentTagValue!));
        return this;
    }

    /// <summary>
    /// Add event type conditions for the current tag. Each type becomes a separate Or condition
    /// with the current tag value.
    /// </summary>
    public EventTagQuery AndEventsOfType<T1, T2>()
    {
        EnsureCurrentTag();
        _conditions.Add(new EventTagQueryCondition(typeof(T1), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T2), _currentTagType!, _currentTagValue!));
        return this;
    }

    /// <summary>
    /// Add event type conditions for the current tag. Each type becomes a separate Or condition
    /// with the current tag value.
    /// </summary>
    public EventTagQuery AndEventsOfType<T1, T2, T3>()
    {
        EnsureCurrentTag();
        _conditions.Add(new EventTagQueryCondition(typeof(T1), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T2), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T3), _currentTagType!, _currentTagValue!));
        return this;
    }

    /// <summary>
    /// Add event type conditions for the current tag. Each type becomes a separate Or condition
    /// with the current tag value.
    /// </summary>
    public EventTagQuery AndEventsOfType<T1, T2, T3, T4>()
    {
        EnsureCurrentTag();
        _conditions.Add(new EventTagQueryCondition(typeof(T1), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T2), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T3), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T4), _currentTagType!, _currentTagValue!));
        return this;
    }

    /// <summary>
    /// Add event type conditions for the current tag. Each type becomes a separate Or condition
    /// with the current tag value.
    /// </summary>
    public EventTagQuery AndEventsOfType<T1, T2, T3, T4, T5>()
    {
        EnsureCurrentTag();
        _conditions.Add(new EventTagQueryCondition(typeof(T1), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T2), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T3), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T4), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T5), _currentTagType!, _currentTagValue!));
        return this;
    }

    /// <summary>
    /// Add event type conditions for the current tag. Each type becomes a separate Or condition
    /// with the current tag value.
    /// </summary>
    public EventTagQuery AndEventsOfType<T1, T2, T3, T4, T5, T6>()
    {
        EnsureCurrentTag();
        _conditions.Add(new EventTagQueryCondition(typeof(T1), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T2), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T3), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T4), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T5), _currentTagType!, _currentTagValue!));
        _conditions.Add(new EventTagQueryCondition(typeof(T6), _currentTagType!, _currentTagValue!));
        return this;
    }

    private void EnsureCurrentTag()
    {
        if (_currentTagValue == null || _currentTagType == null)
        {
            throw new InvalidOperationException(
                "AndEventsOfType must be called after For() or Or() to establish the current tag context.");
        }
    }
}
