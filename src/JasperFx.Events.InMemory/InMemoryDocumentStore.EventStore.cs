using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The prototyping store as a <see cref="IEventStore"/> (jasperfx#985), so the store-agnostic helpers written
/// against it -- Bobcat's arrange and assert steps, <c>services.EventStore()</c> -- work on a stub-first app
/// before it has chosen Marten, Polecat or Fisher.
/// </summary>
/// <remarks>
/// <para>
/// One database, opened as a session on this store; the read-only tier is a session's <c>Events</c>, as on
/// Marten and Polecat. Projections run inline inside the commit, so there are no async shards, nothing is
/// ever stale, and the daemon-facing members refuse with a <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public partial class InMemoryDocumentStore : IEventStore<IInMemoryDocumentSession, IInMemoryQuerySession>
{
    private InMemoryEventDatabase? _database;

    internal InMemoryEventDatabase Database => _database ??= new InMemoryEventDatabase(this);

    // ---- IEventStore ----

    /// <inheritdoc />
    public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token)
        => Task.FromResult<EventStoreUsage?>(new EventStoreUsage(Subject, this)
        {
            Database = new DatabaseUsage { Cardinality = DatabaseCardinality.Single }
        });

    /// <inheritdoc />
    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(string? tenantIdOrDatabaseIdentifier = null,
        ILogger? logger = null)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    /// <inheritdoc />
    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    /// <inheritdoc />
    public Meter Meter { get; } = new("JasperFx.Events.InMemory");

    /// <inheritdoc />
    public ActivitySource ActivitySource { get; } = new("JasperFx.Events.InMemory");

    /// <inheritdoc />
    public string MetricsPrefix => "inmemory";

    /// <inheritdoc />
    public DatabaseCardinality DatabaseCardinality => DatabaseCardinality.Single;

    /// <summary>Events are single-tenanted on the prototyping store.</summary>
    public bool HasMultipleTenants => false;

    /// <inheritdoc />
    public EventStoreIdentity Identity { get; } = new("main", "inmemory");

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IEventDatabase>> AllDatabases()
        => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>([Database]);

    /// <summary>
    /// The read-only event store: a query session's <see cref="InMemoryEventOperations"/>, which is also the
    /// session's <see cref="IQueryEventStore"/>, so a caller can aggregate through it as on Marten.
    /// </summary>
    public IReadOnlyEventStore OpenReadOnlyEventStore() => QuerySession().Events;

    /// <inheritdoc />
    public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
        => throw InMemoryEventRegistry.NotSupported("Compacting a stream");

    /// <inheritdoc />
    public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
        => throw InMemoryEventRegistry.NotSupported("Compacting a stream");

    // ---- IEventStore<TOperations, TQuerySession> ----

    IEventRegistry IEventStore<IInMemoryDocumentSession, IInMemoryQuerySession>.Registry => Events;

    /// <inheritdoc />
    public Type IdentityTypeForProjectedType(Type aggregateType)
        => InMemoryAggregateIdentity.ResolveIdType(aggregateType, Events.StreamIdentity);

    /// <inheritdoc />
    public string DefaultDatabaseName => InMemoryEventDatabase.Name;

    /// <inheritdoc />
    public ErrorHandlingOptions ContinuousErrors { get; } = new();

    /// <inheritdoc />
    public ErrorHandlingOptions RebuildErrors { get; } = new();

    /// <inheritdoc />
    public ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode)
        => mode == ShardExecutionMode.Rebuild ? RebuildErrors : ContinuousErrors;

    /// <summary>None: every projection on the prototyping store is inline.</summary>
    public IReadOnlyList<AsyncShard<IInMemoryDocumentSession, IInMemoryQuerySession>> AllShards() => [];

    /// <inheritdoc />
    public TimeProvider TimeProvider => TimeProvider.System;

    /// <summary>There is no schema to create.</summary>
    public AutoCreate AutoCreateSchemaObjects => AutoCreate.None;

    /// <inheritdoc />
    public IInMemoryDocumentSession OpenSession(IEventDatabase database) => LightweightSession();

    /// <inheritdoc />
    public IInMemoryDocumentSession OpenSession(IEventDatabase database, string tenantId)
        => LightweightSession(tenantId);

    /// <inheritdoc />
    public Task RewindSubscriptionProgressAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token, long? sequenceFloor)
        => throw InMemoryEventRegistry.NotSupported("Subscriptions");

    /// <inheritdoc />
    public Task RewindAgentProgressAsync(IEventDatabase database, string shardName, CancellationToken token,
        long sequenceFloor)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    /// <inheritdoc />
    public Task TeardownExistingProjectionStateAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    /// <inheritdoc />
    public Task DeleteProjectionProgressAsync(IEventDatabase database, string subscriptionName,
        CancellationToken token)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    /// <inheritdoc />
    public ValueTask<IProjectionBatch<IInMemoryDocumentSession, IInMemoryQuerySession>> StartProjectionBatchAsync(
        EventRange range, IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions,
        CancellationToken token)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    /// <inheritdoc />
    public IEventLoader BuildEventLoader(IEventDatabase database, ILogger loggerFactory, EventFilterable filtering,
        AsyncOptions shardOptions)
        => throw InMemoryEventRegistry.NotSupported("The async daemon");

    // ---- the reads behind the read-only tier ----

    // Everything but the tag (DCB) filters, which the prototyping store does not record
    internal const EventQueryFilters SupportedEventQueryFilters = EventQueryFilters.All & ~EventQueryFilters.Tags;

    internal PagedEvents QueryEvents(EventQuery query)
    {
        query.AssertFiltersAreSupported(SupportedEventQueryFilters);

        var typeNames = query.CombinedEventTypeNames();

        List<IEvent> matching;
        using (EnterReadGate())
        {
            matching = _events
                .Where(x => typeNames.Count == 0 || typeNames.Contains(x.EventTypeName))
                .Where(x => query.StreamId is null || streamIdMatches(x, query.StreamId))
                .Where(x => query.CorrelationId is null || x.CorrelationId == query.CorrelationId)
                .Where(x => query.CausationId is null || x.CausationId == query.CausationId)
                .Where(x => query.UserName is null || x.UserName == query.UserName)
                .Where(x => query.TenantId is null || x.TenantId == query.TenantId)
                .Where(x => query.TimestampFrom is null || x.Timestamp >= query.TimestampFrom.Value)
                .Where(x => query.TimestampTo is null || x.Timestamp <= query.TimestampTo.Value)
                .Where(x => query.SequenceFloor is null || x.Sequence >= query.SequenceFloor.Value)
                .Where(x => query.SequenceCeiling is null || x.Sequence <= query.SequenceCeiling.Value)
                .OrderBy(x => x.Sequence)
                .ToList();
        }

        // In long arithmetic, so a caller asking for "everything" with PageSize = int.MaxValue cannot overflow
        var pageNumber = Math.Max(query.PageNumber, 1);
        var pageSize = Math.Max(query.PageSize, 1);
        var skip = (long)(pageNumber - 1) * pageSize;

        return new PagedEvents
        {
            Events = matching.Skip((int)Math.Min(skip, int.MaxValue)).Take(pageSize).ToList(),
            TotalCount = matching.Count,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    private bool streamIdMatches(IEvent @event, string streamId)
        => Events.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.TryParse(streamId, out var id) && @event.StreamId == id
            : @event.StreamKey == streamId;

    internal IReadOnlyList<StreamState> StreamStates(string? tenantId)
    {
        using (EnterReadGate())
        {
            return _streams.Values
                .Where(x => tenantId is null || x.TenantId == tenantId)
                .Select(x => x.Id is Guid id
                    ? new StreamState(id, x.Version, x.AggregateType, x.LastTimestamp, x.Created)
                    : new StreamState((string)x.Id, x.Version, x.AggregateType, x.LastTimestamp, x.Created))
                .ToList();
        }
    }

    internal long HighestEventSequence()
    {
        using (EnterReadGate())
        {
            return _events.Count == 0 ? 0 : _events[^1].Sequence;
        }
    }

    internal long? EventSequenceFloorAt(DateTimeOffset timestamp)
    {
        using (EnterReadGate())
        {
            var below = _events.Where(x => x.Timestamp < timestamp).ToList();
            return below.Count == 0 ? null : below.Max(x => x.Sequence);
        }
    }
}
