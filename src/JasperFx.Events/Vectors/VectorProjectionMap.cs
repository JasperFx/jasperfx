namespace JasperFx.Events.Vectors;

/// <summary>
///     Declares which events contribute an embedding's text, where that text comes from, and which
///     events retract a document from the index (jasperfx#841).
/// </summary>
/// <typeparam name="TId">The identity of the document being embedded.</typeparam>
/// <remarks>
///     <para>
///         The store-independent half of a vector projection. Fisher and Marten.PgVector each wrote
///         their own and had already drifted: Marten's content selector saw the event BODY while
///         Fisher's saw the <see cref="IEvent{T}" /> wrapper; Marten was Guid-only while Fisher's id
///         was open; Marten deleted by stream id by default while Fisher required a selector. One map
///         is what stops a fourth port from being a fourth dialect.
///     </para>
///     <para>
///         <b><see cref="MapFromAggregate{TAggregate}" /> is the reason this type exists rather than
///         just being shared.</b> A content selector that sees only one event cannot keep an embedding
///         correct across partial-update events: given <c>MemoryRevised { Title = null, Body = "new",
///         Tags = null }</c> where null means "unchanged", returning the new body re-embeds the
///         document without its title and tags, and returning null leaves the embedding stale. Both
///         answers are wrong, and there was no third one.
///     </para>
/// </remarks>
public sealed class VectorProjectionMap<TId> where TId : notnull
{
    private readonly Dictionary<Type, Mapping> _content = new();
    private readonly Dictionary<Type, Func<IEvent, TId>> _deletes = new();
    private readonly Dictionary<Type, Func<IEvent, TId>> _aggregateTriggers = new();
    private Func<object, string?>? _aggregateContent;

    /// <summary>True when nothing at all has been declared, which a store should refuse to register.</summary>
    public bool IsEmpty => _content.Count == 0 && _deletes.Count == 0 && _aggregateTriggers.Count == 0;

    /// <summary>The aggregate type content is built from, or null when no aggregate mapping was declared.</summary>
    public Type? AggregateType { get; private set; }

    /// <summary>Every event type that can produce content, from either mapping form.</summary>
    public IEnumerable<Type> ContentEventTypes => _content.Keys.Concat(_aggregateTriggers.Keys);

    /// <summary>Every event type that retracts a document.</summary>
    public IEnumerable<Type> DeleteEventTypes => _deletes.Keys;

    /// <summary>An event that carries text worth embedding, and the document id it belongs to.</summary>
    /// <remarks>
    ///     The selector receives the <see cref="IEvent{T}" /> wrapper rather than the bare body, so
    ///     metadata — timestamp, headers, stream id, version — can contribute to the embedded text or
    ///     to the identity. Fisher's shape rather than Marten's, because the wrapper can always reach
    ///     the body and the body can never reach the wrapper.
    /// </remarks>
    public VectorProjectionMap<TId> Map<TEvent>(
        Func<IEvent<TEvent>, string?> content,
        Func<IEvent<TEvent>, TId> id) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(id);

        AssertNotAlreadyMapped<TEvent>();

        _content[typeof(TEvent)] = new Mapping(
            e => content((IEvent<TEvent>)e),
            e => id((IEvent<TEvent>)e));

        return this;
    }

    /// <summary>
    ///     Build the embedded text from the CURRENT STATE of an aggregate rather than from one event.
    /// </summary>
    /// <param name="content">
    ///     The text to embed, from the aggregate as it stands after the page's events. Returning null
    ///     means "nothing to index for this document", the same as on <see cref="Map{TEvent}" />.
    /// </param>
    /// <param name="triggers">
    ///     The event types that make a document's content worth rebuilding, each paired with the
    ///     document id it names — usually <c>e =&gt; e.StreamId</c>.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠️ <b>Where the aggregate comes from is the store's decision and the ordering trap lives
    ///         there.</b> The two sound options are the inline snapshot, which a store must refuse to
    ///         register unless <typeparamref name="TAggregate" /> really is projected inline, and live
    ///         aggregation up to the page's last event, which costs one read per affected stream per
    ///         page. Reading an ASYNC snapshot is the wrong one: the daemon does not order shards
    ///         against each other, so a vector projection on one shard can see a snapshot another shard
    ///         has not caught up to yet, and the embedding is then silently built from stale state.
    ///     </para>
    ///     <para>
    ///         Content hashing does the rest: an event that turns out not to change the built text
    ///         costs no model call at all, which is the common case precisely when the events are
    ///         partial updates.
    ///     </para>
    /// </remarks>
    public VectorProjectionMap<TId> MapFromAggregate<TAggregate>(
        Func<TAggregate, string?> content,
        params (Type EventType, Func<IEvent, TId> Id)[] triggers) where TAggregate : class
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(triggers);

        if (AggregateType is not null)
        {
            throw new InvalidOperationException(
                $"Content is already built from '{AggregateType.Name}'. A second aggregate mapping would "
                + "leave which one builds the text depending on registration order.");
        }

        if (triggers.Length == 0)
        {
            throw new ArgumentException(
                "At least one triggering event type is required. Without one nothing would ever cause "
                + "the aggregate's content to be rebuilt, so the index would never be written.",
                nameof(triggers));
        }

        foreach (var (eventType, id) in triggers)
        {
            ArgumentNullException.ThrowIfNull(eventType);
            ArgumentNullException.ThrowIfNull(id);

            if (_content.ContainsKey(eventType) || _aggregateTriggers.ContainsKey(eventType))
            {
                throw new InvalidOperationException(
                    $"'{eventType.Name}' is already mapped for content. Two content mappings for one "
                    + "event type would leave which one wins depending on registration order.");
            }

            if (_deletes.ContainsKey(eventType))
            {
                throw new InvalidOperationException(
                    $"'{eventType.Name}' is mapped for deletion, so one event would both write and "
                    + "remove the same document and the outcome would depend on which branch ran first.");
            }

            _aggregateTriggers[eventType] = id;
        }

        AggregateType = typeof(TAggregate);
        _aggregateContent = aggregate => content((TAggregate)aggregate);

        return this;
    }

    /// <summary>
    ///     An event that retracts a document from the index.
    /// </summary>
    /// <remarks>
    ///     ⚠️ <b>There is deliberately no overload without an id selector.</b> Marten's template reads
    ///     <c>@event.StreamId</c> unconditionally and ignores the configured selector, so a projection
    ///     keyed on a payload member deletes nothing and the row stays in the index forever. Making the
    ///     write and the delete structurally incapable of disagreeing beats checking that they agree,
    ///     and the common case costs <c>e =&gt; e.StreamId</c>.
    /// </remarks>
    public VectorProjectionMap<TId> Delete<TEvent>(Func<IEvent<TEvent>, TId> id) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_content.ContainsKey(typeof(TEvent)) || _aggregateTriggers.ContainsKey(typeof(TEvent)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TEvent).Name}' is mapped for content and for deletion, so one event would "
                + "both write and remove the same document and the outcome would depend on which "
                + "branch ran first.");
        }

        if (!_deletes.TryAdd(typeof(TEvent), e => id((IEvent<TEvent>)e)))
        {
            throw new InvalidOperationException($"'{typeof(TEvent).Name}' is already mapped for deletion.");
        }

        return this;
    }

    /// <summary>The id this event retracts, when it retracts one.</summary>
    public bool TryDelete(IEvent @event, out TId id)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (_deletes.TryGetValue(@event.EventType, out var selector))
        {
            id = selector(@event);
            return true;
        }

        id = default!;
        return false;
    }

    /// <summary>The id and text this event contributes directly, when it contributes any.</summary>
    /// <remarks>
    ///     ⚠️ A selector that throws is NOT caught here, and that is the point. Marten's template
    ///     catches everything a selector throws and returns null, which the caller reads as "no content
    ///     for this event" — so a selector with a bug drops the document out of the index with nothing
    ///     reported anywhere. A throw faults the shard, which is what the daemon's error handling is
    ///     for and is the only outcome an operator can act on.
    /// </remarks>
    public bool TryContent(IEvent @event, out TId id, out string? content)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (_content.TryGetValue(@event.EventType, out var mapping))
        {
            id = mapping.Id(@event);
            content = mapping.Content(@event);
            return true;
        }

        id = default!;
        content = null;
        return false;
    }

    /// <summary>The id whose aggregate this event makes worth rebuilding, when it is a trigger.</summary>
    public bool TryAggregateTrigger(IEvent @event, out TId id)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (_aggregateTriggers.TryGetValue(@event.EventType, out var selector))
        {
            id = selector(@event);
            return true;
        }

        id = default!;
        return false;
    }

    /// <summary>The text to embed for a loaded aggregate.</summary>
    public string? ContentFromAggregate(object aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        if (_aggregateContent is null)
        {
            throw new InvalidOperationException(
                $"No aggregate mapping was declared, so there is nothing to build content with. Call "
                + $"{nameof(MapFromAggregate)} first.");
        }

        return _aggregateContent(aggregate);
    }

    private void AssertNotAlreadyMapped<TEvent>()
    {
        if (_content.ContainsKey(typeof(TEvent)) || _aggregateTriggers.ContainsKey(typeof(TEvent)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TEvent).Name}' is already mapped for content. Two content mappings for one "
                + "event type would leave which one wins depending on registration order.");
        }

        if (_deletes.ContainsKey(typeof(TEvent)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TEvent).Name}' is mapped for deletion, so one event would both write and "
                + "remove the same document and the outcome would depend on which branch ran first.");
        }
    }

    private sealed record Mapping(Func<IEvent, string?> Content, Func<IEvent, TId> Id);
}
