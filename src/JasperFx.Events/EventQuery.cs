using JasperFx.Events.Tags;

namespace JasperFx.Events;

/// <summary>
/// Store-agnostic, wire-serializable description of a cross-stream event query, consumed by
/// <see cref="IReadOnlyEventStore.QueryEventsAsync"/>. Every filter member is optional; a null
/// (or empty) member applies no filter. All supplied filters are combined with AND.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering contract:</b> results are always ordered by the store-global event sequence,
/// ascending — oldest first — and <see cref="PageNumber"/>/<see cref="PageSize"/> page through
/// that ordering. See <see cref="IReadOnlyEventStore.QueryEventsAsync"/>.
/// </para>
/// <para>
/// <b>Guard rail (jasperfx#737):</b> an implementation that does not honor a supplied filter MUST
/// refuse it with a <see cref="NotSupportedException"/> naming the field — never silently ignore
/// it, because a silently-ignored filter returns unfiltered results that read as filtered. Call
/// <see cref="AssertFiltersAreSupported"/> first thing in an implementation to get that behavior
/// consistently.
/// </para>
/// </remarks>
public class EventQuery
{
    /// <summary>
    /// Optional exact-match filter on a single event type name (the store's persisted alias for the
    /// event type). Null applies no filter. When <see cref="EventTypeNames"/> is also supplied, the
    /// effective filter is the union of this name and that list — see
    /// <see cref="CombinedEventTypeNames"/>.
    /// </summary>
    public string? EventTypeName { get; set; }

    /// <summary>
    /// Optional filter on multiple event type names (the store's persisted aliases, exact match).
    /// An event matches when its type name equals any entry. Empty applies no filter. When
    /// <see cref="EventTypeName"/> is also supplied, the effective filter is the union of the two —
    /// see <see cref="CombinedEventTypeNames"/>. See jasperfx#737.
    /// </summary>
    public List<string> EventTypeNames { get; set; } = new();

    /// <summary>
    /// Optional exact-match filter on the string form of the stream identity (Guid or string key).
    /// Null applies no filter.
    /// </summary>
    public string? StreamId { get; set; }

    /// <summary>
    /// 1-based page number into the sequence-ascending ordering.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>
    /// Page size for the paged result.
    /// </summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Optional exact-match filter on the event's correlation id metadata. Null applies no filter. Only
    /// honored when the store advertises and captures the correlation id metadata column. See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Optional exact-match filter on the event's causation id metadata. Null applies no filter. Only
    /// honored when the store advertises and captures the causation id metadata column. See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? CausationId { get; set; }

    /// <summary>
    /// Optional exact-match filter on the event's user name metadata. Null applies no filter. Only honored
    /// when the store advertises and captures the user name metadata column (cross-engine). See
    /// JasperFx/CritterWatch #629.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Optional tenant partition to scope the query to. Null means store-global (today's behavior). On a
    /// conjoined <c>UseTenantPartitionedEvents</c> store an untenanted query reads an ambiguous cross-tenant
    /// union, so the Event Explorer sets this to scope the events/metadata query to a single tenant. Only
    /// honored by stores that implement multi-tenancy; a store without a tenant dimension ignores it. See
    /// jasperfx#555 (companion to the jasperfx#503 stream-read overloads).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Optional inclusive lower bound on the event's server-assigned timestamp: events at exactly this
    /// timestamp are included. Null applies no floor. Pairs with <see cref="TimestampTo"/> to form a
    /// time window. See jasperfx#737.
    /// </summary>
    public DateTimeOffset? TimestampFrom { get; set; }

    /// <summary>
    /// Optional inclusive upper bound on the event's server-assigned timestamp: events at exactly this
    /// timestamp are included. Null applies no ceiling. Pairs with <see cref="TimestampFrom"/> to form
    /// a time window. See jasperfx#737.
    /// </summary>
    public DateTimeOffset? TimestampTo { get; set; }

    /// <summary>
    /// Optional inclusive lower bound on the store-global event sequence: the event at exactly this
    /// sequence is included. Null applies no floor. Pairs with
    /// <c>IEventDatabase.FindEventStoreFloorAtTimeAsync</c>, which produces a sequence number this
    /// member can consume. See jasperfx#737.
    /// </summary>
    public long? SequenceFloor { get; set; }

    /// <summary>
    /// Optional inclusive upper bound on the store-global event sequence: the event at exactly this
    /// sequence is included. Null applies no ceiling. See jasperfx#737.
    /// </summary>
    public long? SequenceCeiling { get; set; }

    /// <summary>
    /// Optional tag conditions in the wire-serializable <see cref="EventTagQuerySpec"/> form
    /// (jasperfx#545): the spec's OR'd conditions select the events, and that selection is then
    /// AND-combined with every other filter on this query. Null applies no tag filter. Folded into
    /// <see cref="EventQuery"/> deliberately rather than shipped as a parallel read-tier method — one
    /// abstraction method, one wire shape, one compliance surface. See jasperfx#737.
    /// </summary>
    public EventTagQuerySpec? TagConditions { get; set; }

    /// <summary>
    /// The effective event type name filter: the union of <see cref="EventTypeName"/> and
    /// <see cref="EventTypeNames"/>, distinct, in declaration order with the single name first.
    /// Empty means no event type filter. Implementations should consume this rather than reading
    /// the two members separately, so both spellings share one code path.
    /// </summary>
    public IReadOnlyList<string> CombinedEventTypeNames()
    {
        if (EventTypeName == null && EventTypeNames.Count == 0)
        {
            return [];
        }

        var names = new List<string>(EventTypeNames.Count + 1);
        if (EventTypeName != null)
        {
            names.Add(EventTypeName);
        }

        foreach (var name in EventTypeNames)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// The filters actually supplied on this query — the fields an implementation must either honor
    /// or refuse. Paging (<see cref="PageNumber"/>/<see cref="PageSize"/>) and the sequence-ascending
    /// ordering are unconditional contract, not filters, so they never appear here.
    /// </summary>
    public EventQueryFilters SpecifiedFilters
    {
        get
        {
            var filters = EventQueryFilters.None;

            if (EventTypeName != null) filters |= EventQueryFilters.EventTypeName;
            if (EventTypeNames.Count > 0) filters |= EventQueryFilters.EventTypeNames;
            if (StreamId != null) filters |= EventQueryFilters.StreamId;
            if (CorrelationId != null) filters |= EventQueryFilters.CorrelationId;
            if (CausationId != null) filters |= EventQueryFilters.CausationId;
            if (UserName != null) filters |= EventQueryFilters.UserName;
            if (TenantId != null) filters |= EventQueryFilters.TenantId;
            if (TimestampFrom != null) filters |= EventQueryFilters.TimestampFrom;
            if (TimestampTo != null) filters |= EventQueryFilters.TimestampTo;
            if (SequenceFloor != null) filters |= EventQueryFilters.SequenceFloor;
            if (SequenceCeiling != null) filters |= EventQueryFilters.SequenceCeiling;
            if (TagConditions != null) filters |= EventQueryFilters.TagConditions;

            return filters;
        }
    }

    private static readonly (EventQueryFilters Filter, string Name)[] _filterNames =
    [
        (EventQueryFilters.EventTypeName, nameof(EventTypeName)),
        (EventQueryFilters.EventTypeNames, nameof(EventTypeNames)),
        (EventQueryFilters.StreamId, nameof(StreamId)),
        (EventQueryFilters.CorrelationId, nameof(CorrelationId)),
        (EventQueryFilters.CausationId, nameof(CausationId)),
        (EventQueryFilters.UserName, nameof(UserName)),
        (EventQueryFilters.TenantId, nameof(TenantId)),
        (EventQueryFilters.TimestampFrom, nameof(TimestampFrom)),
        (EventQueryFilters.TimestampTo, nameof(TimestampTo)),
        (EventQueryFilters.SequenceFloor, nameof(SequenceFloor)),
        (EventQueryFilters.SequenceCeiling, nameof(SequenceCeiling)),
        (EventQueryFilters.TagConditions, nameof(TagConditions))
    ];

    /// <summary>
    /// The jasperfx#737 guard rail, centralized: throw <see cref="NotSupportedException"/> naming
    /// every supplied filter that is not in <paramref name="supportedFilters"/>. An implementation
    /// of <see cref="IReadOnlyEventStore.QueryEventsAsync"/> calls this first thing, declaring the
    /// fields it honors (typically <see cref="EventQueryFilters.All"/> once fully implemented), so
    /// a query carrying a filter the store has not implemented fails loudly instead of returning
    /// unfiltered results that read as filtered.
    /// </summary>
    /// <param name="supportedFilters">The set of filters the calling implementation honors.</param>
    /// <exception cref="NotSupportedException">A filter was supplied that the implementation did not declare.</exception>
    public void AssertFiltersAreSupported(EventQueryFilters supportedFilters)
    {
        var unsupported = SpecifiedFilters & ~supportedFilters;
        if (unsupported == EventQueryFilters.None)
        {
            return;
        }

        var names = _filterNames
            .Where(x => unsupported.HasFlag(x.Filter))
            .Select(x => $"{nameof(EventQuery)}.{x.Name}");

        throw new NotSupportedException(
            $"This event store does not support the supplied event query filter(s) {string.Join(", ", names)}. " +
            "A supplied filter must be honored or refused, never silently ignored — unfiltered results would read as filtered. See jasperfx#737.");
    }
}

/// <summary>
/// The individual filter fields of an <see cref="EventQuery"/>, as flags, so an implementation of
/// <see cref="IReadOnlyEventStore.QueryEventsAsync"/> can declare which fields it honors through
/// <see cref="EventQuery.AssertFiltersAreSupported"/>. See jasperfx#737.
/// </summary>
[Flags]
public enum EventQueryFilters
{
    None = 0,
    EventTypeName = 1 << 0,
    StreamId = 1 << 1,
    CorrelationId = 1 << 2,
    CausationId = 1 << 3,
    UserName = 1 << 4,
    TenantId = 1 << 5,
    EventTypeNames = 1 << 6,
    TimestampFrom = 1 << 7,
    TimestampTo = 1 << 8,
    SequenceFloor = 1 << 9,
    SequenceCeiling = 1 << 10,
    TagConditions = 1 << 11,

    /// <summary>
    /// Both halves of the inclusive timestamp window.
    /// </summary>
    TimestampWindow = TimestampFrom | TimestampTo,

    /// <summary>
    /// Both halves of the inclusive sequence window.
    /// </summary>
    SequenceWindow = SequenceFloor | SequenceCeiling,

    /// <summary>
    /// The pre-jasperfx#737 exact-match surface.
    /// </summary>
    Baseline = EventTypeName | StreamId | CorrelationId | CausationId | UserName | TenantId,

    /// <summary>
    /// Every filter <see cref="EventQuery"/> can carry. What a fully implemented store declares.
    /// </summary>
    All = Baseline | EventTypeNames | TimestampWindow | SequenceWindow | TagConditions
}

/// <summary>
/// The paged result of <see cref="IReadOnlyEventStore.QueryEventsAsync"/>.
/// </summary>
public class PagedEvents
{
    /// <summary>
    /// The requested page of matching events, ordered by store-global sequence ascending.
    /// </summary>
    public IReadOnlyList<IEvent> Events { get; set; } = [];

    /// <summary>
    /// The total number of events matching the query's filters across every page, not the size of
    /// this page and not the size of the store.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Echo of <see cref="EventQuery.PageNumber"/>.
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// Echo of <see cref="EventQuery.PageSize"/>.
    /// </summary>
    public int PageSize { get; set; }
}
