using JasperFx.Core;

namespace JasperFx.Events.Tags;

/// <summary>
///     The shared <see cref="IEventBoundary{T}"/>: a writable handle over every stream whose events
///     match a tag query. A store builds one in its <c>FetchForWritingByTags</c> from the aggregate and
///     events it read; nothing here reads from the store.
/// </summary>
/// <remarks>
///     <para>
///     <b>Every write goes through the public <see cref="IEventOperations"/> (jasperfx#1044)</b>, where
///     Marten's and Polecat's copies built a <see cref="StreamAction"/> and put it in the session's work
///     tracker themselves. That is what lets one type serve every store, and it is why this class has no
///     tenant handling: <c>Append</c> and <c>StartStream</c> stamp the stream with the tenant of the
///     operations they are called on. Doing it here went wrong in two different ways. Marten left
///     <see cref="StreamAction.TenantId"/> null, so a conjoined store read the stream's state with no
///     tenant filter and the save then stamped its own tenant over a boundary fetched through
///     <c>ForTenant</c>. Polecat stamped the session's tenant, which under <c>ForTenant</c> is the
///     parent's.
///     </para>
///     <para>
///     <b>So a store must pass the operations the boundary was fetched through</b>, the tenant-scoped
///     ones under <c>ForTenant</c>, and not those of its root session.
///     </para>
///     <para>
///     <b>The consistency check does not live here either.</b> It stays per store on purpose: tag-version
///     tables in Marten and Polecat, a re-query under the write lock in Fisher. This type only reports
///     <see cref="LastSeenSequence"/> and the store decides what to do about it at
///     <c>SaveChangesAsync</c>, so the appends below carry no expected version and take no lock.
///     </para>
/// </remarks>
/// <typeparam name="T">The aggregate type built from the events matching the tag query.</typeparam>
public sealed class EventBoundary<T> : IEventBoundary<T> where T : class
{
    private readonly IEventOperations _operations;
    private readonly IEventRegistry _events;
    private readonly IReadOnlyList<ITagTypeRegistration> _tagTypes;

    /// <param name="operations">
    ///     The operations this boundary was fetched through, which decide the tenant of what it appends.
    /// </param>
    /// <param name="events">
    ///     The store's event configuration. Its <see cref="IEventRegistry.BuildEvent"/> has to pass an
    ///     already built <see cref="IEvent"/> through, as every store's does for its own <c>Append</c>.
    /// </param>
    /// <param name="tagTypes">The store's registered tag types.</param>
    /// <param name="aggregate">The aggregate built from <paramref name="loadedEvents"/>, or null if none matched.</param>
    /// <param name="loadedEvents">The events matching the tag query, ordered by sequence.</param>
    public EventBoundary(IEventOperations operations, IEventRegistry events,
        IReadOnlyList<ITagTypeRegistration> tagTypes, T? aggregate, IReadOnlyList<IEvent> loadedEvents)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(tagTypes);
        ArgumentNullException.ThrowIfNull(loadedEvents);

        _operations = operations;
        _events = events;
        _tagTypes = tagTypes;
        Aggregate = aggregate;
        Events = loadedEvents;

        // Zero when nothing matched, which still enforces consistency: any matching event appearing
        // later has a sequence above zero. That is what makes a boundary over an empty result a usable
        // "this must not exist yet" assertion.
        LastSeenSequence = loadedEvents.Count == 0 ? 0 : loadedEvents.Max(x => x.Sequence);
    }

    /// <inheritdoc />
    public T? Aggregate { get; }

    /// <inheritdoc />
    public long LastSeenSequence { get; }

    /// <inheritdoc />
    public IReadOnlyList<IEvent> Events { get; }

    /// <inheritdoc />
    public void AppendOne(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Through the registry even when the caller already wrapped it, because BuildEvent is where a
        // store derives what else an event carries: Marten's tag rules run there, and have to have run
        // before the tags are read below.
        var wrapped = _events.BuildEvent(@event);
        var tags = ResolveTags(wrapped);

        if (!TryAppendToTaggedStream(wrapped, tags))
        {
            StartOwnStream(wrapped);
        }
    }

    /// <inheritdoc />
    public void AppendMany(params object[] events) => AppendMany((IEnumerable<object>)events);

    /// <inheritdoc />
    public void AppendMany(IEnumerable<object> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var @event in events)
        {
            AppendOne(@event);
        }
    }

    /// <summary>
    ///     The event's own tags, or tags inferred from its public properties.
    /// </summary>
    /// <remarks>
    ///     An event with neither is a hard error rather than an untagged append. A boundary has no single
    ///     stream to fall back on, so an untagged event has nowhere to go, and inventing a stream for it
    ///     would put the event somewhere no tag query could ever find it.
    /// </remarks>
    private IReadOnlyList<EventTag> ResolveTags(IEvent @event)
    {
        if (@event.Tags is { Count: > 0 })
        {
            return @event.Tags;
        }

        var inferred = EventTagInference.InferTags(@event.Data, _tagTypes);

        if (inferred.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot append '{@event.Data.GetType().Name}' to a DCB boundary: it carries no tags and none "
                + "could be inferred from its properties. Set them explicitly with WithTag(), or give the event "
                + "a property of a registered tag type.");
        }

        foreach (var tag in inferred)
        {
            @event.AddTag(tag);
        }

        return @event.Tags!;
    }

    /// <summary>
    ///     Append to the stream named by the first tag that can name one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     An event belongs to exactly one stream, though its tags are all written. That stream comes
    ///     from the first tag whose registration names an aggregate type: that tag is the consistency
    ///     boundary the aggregate is built over, so its value <em>is</em> the stream identity. Appending
    ///     rather than starting is deliberate, because the derived stream ordinarily exists already, and
    ///     <c>Append</c> creates it when it does not.
    ///     </para>
    ///     <para>
    ///     A value also has to fit the store's stream identity. Any value can be a string key, through
    ///     the canonical form so that every process derives the same stream. Only a <see cref="Guid"/> can
    ///     be a Guid id, and <see cref="ITagTypeRegistration.ForAggregate{T}"/> is free to name a string
    ///     or numeric tag on such a store, so a tag that cannot be one is passed over rather than handed
    ///     to the store as a stream it cannot hold.
    ///     </para>
    /// </remarks>
    private bool TryAppendToTaggedStream(IEvent @event, IReadOnlyList<EventTag> tags)
    {
        foreach (var tag in tags)
        {
            var registration = FindTagType(tag.TagType);

            if (registration?.AggregateType == null)
            {
                continue;
            }

            var value = registration.ExtractValue(tag.Value);
            StreamAction stream;

            if (_events.StreamIdentity == StreamIdentity.AsString)
            {
                stream = _operations.Append(TagValueStringifier.Stringify(value), @event);
            }
            else if (value is Guid id)
            {
                stream = _operations.Append(id, @event);
            }
            else
            {
                continue;
            }

            // Not overwritten: a stream already pending in this session may have been started as
            // something more specific than the tag's aggregate.
            stream.AggregateType ??= registration.AggregateType;

            return true;
        }

        return false;
    }

    /// <summary>
    ///     Give an event no tag can route its own new stream.
    /// </summary>
    /// <remarks>
    ///     That is not a fallback so much as the honest answer: an event no tag routes belongs to no
    ///     aggregate, and a new stream per event cannot conflict with anything. Its tags are still
    ///     written, so tag queries find it. The stream id is chosen here because <c>StartStream</c>
    ///     without one assumes Guid identity on Polecat.
    /// </remarks>
    private void StartOwnStream(IEvent @event)
    {
        if (_events.StreamIdentity == StreamIdentity.AsString)
        {
            _operations.StartStream(Guid.NewGuid().ToString(), @event);
        }
        else
        {
            _operations.StartStream(CombGuidIdGeneration.NewGuid(), @event);
        }
    }

    private ITagTypeRegistration? FindTagType(Type tagType)
    {
        foreach (var registration in _tagTypes)
        {
            if (registration.TagType == tagType)
            {
                return registration;
            }
        }

        return null;
    }
}
