using ImTools;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

// marten#6003: the slice index (class B). For a multi-stream projection with UseSliceIndex(), every batch records which
// identities each of its events went to, in the batch's own transaction, so the index is exactly what the daemon
// applied. The rows are taken from the page's grouping before anything is taken out of it: the slices of a parked
// identity, and of one being rebuilt, are indexed like every other.
public partial class AggregationRunner<TDoc, TId, TOperations, TQuerySession>
{
    private bool? _writesSliceIndex;
    private long? _sliceIndexStart;
    private DateTimeOffset _lastSliceIndexPrune = DateTimeOffset.MinValue;
    private int _pruningSliceIndex;

    private static Func<TId, object>? _unwrapIdentity;
    private static Type? _simpleIdentityType;

    // The projection's own base class, which knows how to find one identity's events. Null for a wrapper that isn't one.
    private JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>? identityProjection
        => Projection as JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>;

    private IdentitySourcePlan? identitySources => identityProjection?.IdentitySources;

    private bool writesSliceIndex => _writesSliceIndex ??= Projection.Scope == AggregationScope.MultiStream &&
                                                           Projection.Options.SliceIndex != null &&
                                                           identitySources is { SliceIndex: true } &&
                                                           _store.SupportsIdentityEventSource(
                                                               IdentityEventSource.SliceIndex);

    /// <summary>For tests: where this runner believes its slice index starts, once it has read the mark.</summary>
    internal long? SliceIndexStart => _sliceIndexStart;

    private static object unwrapIdentity(TId id)
    {
        _unwrapIdentity ??= x => ParkedIdentityText.Unwrap(x!);
        return _unwrapIdentity(id);
    }

    private static Type simpleIdentityType(object sample) => _simpleIdentityType ??= sample.GetType();

    // Before the binding and the parked filter take anything out of the page
    private List<SliceIndexEntry>? collectSliceIndex(EventRange range)
    {
        if (!writesSliceIndex || range.BatchBehavior != BatchBehavior.Individual) return null;

        var entries = new List<SliceIndexEntry>();
        foreach (var group in range.Groups.OfType<SliceGroup<TDoc, TId>>())
        {
            foreach (var slice in group.Slices)
            {
                var identity = unwrapIdentity(slice.Id);
                long? previous = null;

                // Fan-out children carry their parent's sequence, and the slice is in sequence order
                foreach (var @event in slice.Events())
                {
                    if (previous == @event.Sequence) continue;
                    previous = @event.Sequence;
                    entries.Add(new SliceIndexEntry(group.TenantId, identity, @event.Sequence));
                }
            }
        }

        return entries;
    }

    private void writeSliceIndex(IProjectionBatch batch, ShardName shard, IReadOnlyList<SliceIndexEntry>? entries)
    {
        if (entries is not { Count: > 0 }) return;

        // A slice can only hold an event once, but a page with fan-out across tenants could repeat a row
        var distinct = entries.Distinct().ToList();
        batch.AppendSliceIndex(new SliceIndexRows(shard.Name, shard.Version, simpleIdentityType(distinct[0].Identity),
            distinct));
    }

    // At the first continuous build: where the index starts. A projection that turns the index on for existing data
    // starts it at the shard's position, so every identity parked from now on finds its rows.
    private async Task ensureSliceIndexStartAsync(ShardName shard, long position, CancellationToken token)
    {
        if (!writesSliceIndex || _sliceIndexStart.HasValue) return;

        _sliceIndexStart = await _database
            .TrySetEventRangeMarkAsync(SliceIndexMarks.KeyFor(shard), shard.TenantId, position, token)
            .ConfigureAwait(false);

        if (_sliceIndexStart > 0)
        {
            _logger.LogInformation(
                "The slice index of {Projection} starts at sequence {Start}: replaying a parked identity works now, rebuilding one identity from nothing needs a rebuild of the projection first",
                Projection.Name, _sliceIndexStart);
        }
    }

    private async Task<long?> sliceIndexStartAsync(ShardName shard, CancellationToken token)
    {
        if (_sliceIndexStart.HasValue) return _sliceIndexStart;

        return _sliceIndexStart = await _database
            .FetchEventRangeMarkAsync(SliceIndexMarks.KeyFor(shard), shard.TenantId, token)
            .ConfigureAwait(false);
    }

    // On the shard's heartbeat: drop rows that fell out of a ForParking window, keeping every row a parked identity
    // still needs, and move the index's start up to what was deleted
    async Task IParkedIdentityHost.MaintainSliceIndexAsync(long committed, CancellationToken token)
    {
        if (!writesSliceIndex) return;

        var options = Projection.Options.SliceIndex!;
        if (options.Retention.Window is not { } window) return;

        var shard = _parkingShard;
        if (shard == null || committed <= 0) return;

        var now = _store.TimeProvider.GetUtcNow();
        if (now - _lastSliceIndexPrune < options.PruneInterval) return;
        if (Interlocked.CompareExchange(ref _pruningSliceIndex, 1, 0) != 0) return;

        try
        {
            _lastSliceIndexPrune = now;

            // A park still in a batch has its resume point at that batch's floor, which is at or above the committed
            // position read here
            var keepAfter = committed;
            var parked = _parked;
            if (parked != null)
            {
                foreach (var row in parked.Enumerate())
                {
                    keepAfter = Math.Min(keepAfter, row.Value.ResumeAfter);
                }
            }

            var deleted = await _database.PruneSliceIndexAsync(new SliceIndexPruneRequest
            {
                ProjectionName = shard.Name,
                ProjectionVersion = shard.Version,
                TenantId = shard.TenantId,
                DeleteThrough = keepAfter,
                OlderThan = now - window
            }, token).ConfigureAwait(false);

            if (deleted is { } through && through > (_sliceIndexStart ?? 0))
            {
                await _database.SetEventRangeMarkAsync(SliceIndexMarks.KeyFor(shard), shard.TenantId, through, token)
                    .ConfigureAwait(false);
                _sliceIndexStart = through;

                _logger.LogDebug("Pruned the slice index of {Projection} through sequence {Sequence}", shard.Name,
                    through);
            }
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning(e, "Could not prune the slice index of {Projection}", shard.Name);
        }
        finally
        {
            Interlocked.Exchange(ref _pruningSliceIndex, 0);
        }
    }
}
