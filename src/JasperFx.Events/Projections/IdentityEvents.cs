using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

/// <summary>
/// marten#6003: where the events of one projection identity are read from.
/// </summary>
public enum IdentityEventSource
{
    /// <summary>The identity is a stream: a single-stream projection, or <c>IdentityFromStream&lt;TEvent&gt;()</c>.</summary>
    Stream,

    /// <summary>The identity is a stored DCB tag value: <c>IdentityFromTag&lt;TTag&gt;()</c>.</summary>
    Tag,

    /// <summary>The projection's slice index lists the identity's events: <c>UseSliceIndex()</c>.</summary>
    SliceIndex,

    /// <summary>
    /// Every event of the projection's types in a range, sliced to find the identity's. The only way for a projection
    /// whose grouping can't be inverted, and not exact unless the range starts at zero.
    /// </summary>
    Scan
}

/// <summary>
/// marten#6003, the store contract behind per-identity work on a multi-stream projection: the events one source holds
/// for one identity, in (<see cref="AfterSequence" />, <see cref="Ceiling" />], in sequence order, at most
/// <see cref="Limit" /> of them. Read with the same rules the store's daemon loader applies to the projection: only
/// <see cref="EventTypes" /> when it isn't empty, archived events only when <see cref="IncludeArchivedEvents" /> is set.
/// See <see cref="IEventStore{TOperations,TQuerySession}.LoadEventsForIdentityAsync" />.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="IdentityEventSource.Stream" />: the events of stream <see cref="Identity" /> in <see cref="TenantId" />,
/// as <see cref="IEventStore{TOperations,TQuerySession}.LoadEventsForIdentityRebuildAsync" /> reads them.</item>
/// <item><see cref="IdentityEventSource.Tag" />: the events in <see cref="TenantId" /> carrying a tag of
/// <see cref="TagType" /> whose value is <see cref="IdentityValue" /> (through the tag table or the tag column).</item>
/// <item><see cref="IdentityEventSource.SliceIndex" />: the events whose sequences the slice index of
/// <see cref="ProjectionName" />/<see cref="ProjectionVersion" /> lists for (<see cref="TenantId" />,
/// <see cref="IdentityValue" />), whatever tenant the event itself is in. A row whose event is gone (compacted or
/// tombstoned) is ignored.</item>
/// <item><see cref="IdentityEventSource.Scan" />: every event in the range, in <see cref="TenantId" /> unless it is null.
/// <see cref="Identity" /> is not used.</item>
/// </list>
/// Events come back with their tags when <see cref="IncludeTags" /> is set, and always for
/// <see cref="IdentityEventSource.Tag" />, because the projection slices by them.
/// </remarks>
public sealed class IdentityEventLoadRequest
{
    public IdentityEventLoadRequest(IdentityEventSource source, string? tenantId, object? identity, long afterSequence,
        long ceiling, int limit)
    {
        Source = source;
        TenantId = tenantId;
        Identity = identity;
        AfterSequence = afterSequence;
        Ceiling = ceiling;
        Limit = limit;
        IdentityValue = identity == null ? null : ParkedIdentityText.Unwrap(identity);
    }

    public IdentityEventSource Source { get; }

    /// <summary>The tenant. Null only for a <see cref="IdentityEventSource.Scan" /> across every tenant.</summary>
    public string? TenantId { get; }

    /// <summary>
    /// The identity as the projection knows it: for <see cref="IdentityEventSource.Stream" /> the stream id or key, or a
    /// strong-typed identifier wrapping one.
    /// </summary>
    public object? Identity { get; }

    /// <summary><see cref="Identity" /> unwrapped to its simple value: Guid, string, int or long.</summary>
    public object? IdentityValue { get; }

    /// <summary>Exclusive lower bound on the event sequence.</summary>
    public long AfterSequence { get; }

    /// <summary>Inclusive upper bound on the event sequence.</summary>
    public long Ceiling { get; }

    /// <summary>The most events to return. Fewer means there are no more.</summary>
    public int Limit { get; }

    /// <summary>For <see cref="IdentityEventSource.Tag" />: the registered tag type.</summary>
    public Type? TagType { get; init; }

    /// <summary>For <see cref="IdentityEventSource.SliceIndex" />: the projection whose index to read.</summary>
    public string? ProjectionName { get; init; }

    /// <summary>For <see cref="IdentityEventSource.SliceIndex" />: the projection version whose index to read.</summary>
    public uint ProjectionVersion { get; init; } = 1;

    /// <summary>The event types the projection handles. Empty means every type.</summary>
    public IReadOnlyList<Type> EventTypes { get; init; } = [];

    /// <summary>Whether the projection reads archived events.</summary>
    public bool IncludeArchivedEvents { get; init; }

    /// <summary>
    /// Skip (and dead-letter) an event the store can't deserialize instead of throwing. Off by default, and never taken
    /// from the store's continuous error options; see <see cref="IdentityRebuildEventRequest.SkipSerializationErrors" />.
    /// </summary>
    public bool SkipSerializationErrors { get; init; }

    /// <summary>Hydrate the events' DCB tags, because the projection slices by them.</summary>
    public bool IncludeTags { get; init; }

    public override string ToString()
        => $"{Source} {IdentityValue} (tenant {TenantId ?? "*"}) in ({AfterSequence}, {Ceiling}], limit {Limit}";
}

/// <summary>
/// marten#6003: what <see cref="IEventStore{TOperations,TQuerySession}.LoadIdentityEventsAsync" /> reads: the events one
/// identity of a projection got, a page at a time. The single-identity stepper (CritterWatch) reads these and steps
/// them with <see cref="IEventStore.RunMultiStreamProjectionForIdentityAsync" />.
/// </summary>
public sealed record IdentityEventsRequest
{
    /// <summary>The projection's name.</summary>
    public required string ProjectionName { get; init; }

    /// <summary>The identity, as the projection knows it, or its text.</summary>
    public required object Identity { get; init; }

    /// <summary>The tenant. Null means the default tenant.</summary>
    public string? TenantId { get; init; }

    /// <summary>Read events after this sequence. Start at zero, then pass <see cref="IdentityEventPage.NextAfter" />.</summary>
    public long AfterSequence { get; init; }

    /// <summary>Read no event above this sequence. Null reads up to the newest event.</summary>
    public long? Ceiling { get; init; }

    /// <summary>The most events a page returns. Defaults to 500.</summary>
    public int Limit { get; init; } = 500;

    /// <summary>
    /// For a projection whose identity can only be found by scanning (<see cref="IdentityAccessClass.ScanOnly" />): the
    /// most events of the projection's types one call may read, from <see cref="AfterSequence" /> on. Required for such
    /// a projection, so nobody scans the whole store by accident. Ignored otherwise.
    /// </summary>
    public long? ScanWindow { get; init; }
}

/// <summary>
/// marten#6003: one page of an identity's events (<see cref="IEventStore{TOperations,TQuerySession}.LoadIdentityEventsAsync" />).
/// </summary>
/// <param name="Events">The stored events the projection gave the identity, in sequence order. A fan-out child is not
/// here: its parent is, and slicing the parent again makes the child.</param>
/// <param name="Source">Where they were read from.</param>
/// <param name="Exact">True when the page holds every event of the identity in its range. False for a scan, which only
/// read <see cref="IdentityEventsRequest.ScanWindow" /> events.</param>
/// <param name="NextAfter">Pass this as the next request's <see cref="IdentityEventsRequest.AfterSequence" />.</param>
/// <param name="Truncated">There may be more events after <see cref="NextAfter" />.</param>
public sealed record IdentityEventPage(
    IReadOnlyList<IEvent> Events,
    IdentityEventSource Source,
    bool Exact,
    long NextAfter,
    bool Truncated);

/// <summary>
/// marten#6003: the progress of a per-identity rebuild by scan
/// (<see cref="IProjectionDaemon.RebuildIdentityByScanAsync" />).
/// </summary>
/// <param name="ScannedThrough">The sequence the scan has read through.</param>
/// <param name="Ceiling">The sequence the scan reads to before it binds to a batch.</param>
/// <param name="EventsFound">How many events it has found for the identity so far.</param>
public sealed record IdentityScanProgress(long ScannedThrough, long Ceiling, int EventsFound);
