using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.InMemory.Daemon;

/// <summary>
/// The async projection daemon on the in-memory prototyping store (jasperfx#1201): JasperFx's own daemon over the
/// store's events, progression and documents. It exists so the daemon-side compliance suites have a reference
/// store to run against in this repository.
/// </summary>
internal sealed class InMemoryProjectionDaemon
    : JasperFxAsyncDaemon<IInMemoryDocumentSession, IInMemoryQuerySession, IInMemoryProjection>, IProjectionDaemon
{
    internal InMemoryProjectionDaemon(InMemoryDocumentStore store, InMemoryEventDatabase database, ILogger logger)
        : base(store, database, logger, new InMemoryHighWaterDetector(store, database), store.Projections)
    {
    }
}

/// <summary>
/// Sequences on the prototyping store are assigned inside the commit gate and are contiguous, so everything
/// committed is safe to read.
/// </summary>
internal sealed class InMemoryHighWaterDetector(InMemoryDocumentStore store, InMemoryEventDatabase database)
    : IHighWaterDetector
{
    public Uri DatabaseUri => database.DatabaseUri;

    public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

    public Task<HighWaterStatistics> Detect(CancellationToken token)
    {
        var lastMark = store.ProgressionFor(ShardState.HighWaterMark);
        var highest = store.HighestEventSequence();
        var now = store.Events.TimeProvider.GetUtcNow();

        var statistics = new HighWaterStatistics
        {
            LastMark = lastMark,
            HighestSequence = highest,
            CurrentMark = highest,
            SafeStartMark = highest,
            Timestamp = now
        };

        if (statistics.HasChanged)
        {
            store.MarkHighWater(highest, now);
            statistics.LastUpdated = now;
        }

        return Task.FromResult(statistics);
    }
}

/// <summary>
/// Pages a shard's events out of the store, in sequence order, filtered by the projection's event types.
/// </summary>
internal sealed class InMemoryEventLoader(InMemoryDocumentStore store, EventFilterable filtering) : IEventLoader
{
    public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
        => Task.FromResult(store.LoadPage(request, filtering));
}

/// <summary>
/// One batch of async projection work. The documents the projections write are queued on the batch's sessions
/// and applied, with the shard's progression, in one commit on the store's gate, so a batch is all or nothing.
/// The progression update is floor-checked the way Marten's is: it only applies when the row still holds the
/// range's floor, which is what lets an empty range <c>(C, C]</c> commit as "the shard is still at C".
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Prototyping store; not AOT-compatible.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Prototyping store; not AOT-compatible.")]
internal sealed class InMemoryProjectionBatch : ProjectionBatchBase<InMemoryDocumentSession>,
    IProjectionBatch<IInMemoryDocumentSession, IInMemoryQuerySession>
{
    private readonly InMemoryDocumentStore _store;
    private readonly List<(ShardName Shard, long Floor, long Ceiling)> _progress = new();

    // marten#6003: applied in the commit, with the documents and the progression
    private readonly List<ParkedIdentity> _parks = new();
    private readonly List<ParkedIdentityKey> _unparks = new();

    // marten#6003: the slice index rows, applied in the commit too
    private readonly List<SliceIndexRows> _sliceIndex = new();

    internal InMemoryProjectionBatch(InMemoryDocumentStore store, ShardExecutionMode mode) : base(mode)
    {
        _store = store;
    }

    protected override IMessageOutbox Outbox { get; } = new NoOutbox();

    protected override InMemoryDocumentSession OpenSessionForTenant(string tenantId)
        => _store.ProjectionBatchSession(tenantId);

    IInMemoryDocumentSession IProjectionBatch<IInMemoryDocumentSession, IInMemoryQuerySession>.SessionForTenant(
        string tenantId) => SessionForTenant(tenantId);

    public override ValueTask RecordProgress(EventRange range)
    {
        lock (_progress)
        {
            _progress.Add((range.ShardName, range.SequenceFloor, range.SequenceCeiling));
        }

        return ValueTask.CompletedTask;
    }

    public void ParkIdentity(ParkedIdentity parked)
    {
        ArgumentNullException.ThrowIfNull(parked);
        lock (_parks) _parks.Add(parked);
    }

    public void UnparkIdentity(ParkedIdentityKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_parks) _unparks.Add(key);
    }

    public void AppendSliceIndex(SliceIndexRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_parks) _sliceIndex.Add(rows);
    }

    protected override Task ExecuteOperationsInTransactionAsync(Func<CancellationToken, Task> beforeCommit,
        CancellationToken token)
    {
        var changes = new InMemoryChangeSet();

        return _store.CommitAtomicallyAsync(async set =>
        {
            // First, so a shard that moved underneath this batch fails it before anything is written
            foreach (var (shard, floor, ceiling) in _progress)
            {
                _store.AdvanceProgression(shard, floor, ceiling, set);
            }

            foreach (var session in Sessions)
            {
                foreach (var change in session.DrainProjectionWrites())
                {
                    change(set);
                }
            }

            var raised = RaisedEvents;
            if (raised.Count > 0)
            {
                _store.AppendStreams(raised, Sessions.FirstOrDefault() ?? _store.LightweightSession());
            }

            lock (_parks)
            {
                foreach (var key in _unparks) _store.DeleteParked(key, set);
                foreach (var parked in _parks) _store.WriteParked(parked, set);
                foreach (var rows in _sliceIndex) _store.WriteSliceIndex(rows, set);
            }

            await beforeCommit(token).ConfigureAwait(false);
        }, changes, token);
    }

    // jasperfx#1258: the store's after-commit hooks, which this store only has for tests
    protected override Task AfterCommitAsync()
    {
        var hook = _store.ProjectionBatchCommitted;
        if (hook == null) return Task.CompletedTask;

        ParkedIdentity[] parks;
        lock (_parks) parks = _parks.ToArray();

        return hook(parks);
    }

    private sealed class NoOutbox : IMessageOutbox
    {
        public ValueTask<IMessageBatch> CreateBatch(IDocumentSessionOperations session)
            => throw InMemoryEventRegistry.NotSupported("Publishing messages from async projections");
    }
}
