using System.Diagnostics.CodeAnalysis;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

public class CompositeExecution<TOperations, TQuerySession> : ProjectionExecution<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IReadOnlyList<ExecutionStage> _inners;
    private readonly EventFilterable _filtering;
    private readonly bool _replayEligible;

    public CompositeExecution(ShardName shardName, AsyncOptions options, IEventStore<TOperations, TQuerySession> store, IEventDatabase database, IJasperFxProjection<TOperations> projection, ILogger logger, IReadOnlyList<ExecutionStage> inners, bool replayEligible = true) : base(shardName, options, store, database, projection, logger)
    {
        _inners = inners;
        _replayEligible = replayEligible;

        // The composite projection is itself an EventFilterable; the replay loader uses it to scope the
        // single pass the same way the continuous shard does.
        _filtering = projection as EventFilterable ?? new EventFilterable();
    }

    /// <summary>
    /// jasperfx#684: <see cref="CompositeProjection{TOperations,TQuerySession}.KeepUnchangedMembers" /> applies. Set by
    /// the composite when it builds this execution.
    /// </summary>
    internal bool KeepsUnchangedMembers { get; init; }

    // The position each kept member skips to, read from the members' own progression rows before the first range.
    // Null until then, and always null when members are not kept.
    private Dictionary<ISubscriptionExecution, long>? _keptThrough;

    private async Task<IReadOnlyDictionary<ISubscriptionExecution, long>?> keptThroughAsync(EventRange range,
        CancellationToken token)
    {
        if (!KeepsUnchangedMembers) return null;

        if (_keptThrough == null)
        {
            var kept = new Dictionary<ISubscriptionExecution, long>();
            foreach (var execution in _inners.SelectMany(x => x.Executions))
            {
                if (execution is CompositeExecution<TOperations, TQuerySession>) continue;

                // A member's row only runs ahead of the composite's when the composite was bumped (or rebuilt) and the
                // member was not. In lockstep, which is every range after the replay passes it, the two are equal.
                var position = await _database.ProjectionProgressFor(execution.ShardName, token).ConfigureAwait(false);
                if (position > range.SequenceFloor)
                {
                    kept[execution] = position;
                    _logger.LogInformation(
                        "Composite member {Member} kept its progress at {Position}, so it skips the replay of {Composite} up to there",
                        execution.ShardName.Identity, position, _shardName.Identity);
                }
            }

            _keptThrough = kept;
        }

        // Once every kept member is past its position it is an ordinary member again
        return _keptThrough.Values.Any(x => x > range.SequenceFloor) ? _keptThrough : null;
    }

    public override bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        if (!_replayEligible)
        {
            return false;
        }

        var loader = _store.BuildEventLoader(_database, _logger, _filtering, _options, _shardName);
        executor = new CompositeReplayExecutor(_shardName, loader, this, _database, _options, _logger);
        return true;
    }

    protected override async Task<IProjectionBatch> buildBatchWithNoSkippingAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession>? batch = null;
        try
        {
            batch = await _store.StartProjectionBatchAsync(range, _database, Mode, _options, cancellationToken);

            range.ActiveBatch = batch;

            var keptThrough = await keptThroughAsync(range, cancellationToken).ConfigureAwait(false);

            foreach (var stage in _inners)
            {
                // marten#4729: member executions default to ShardExecutionMode.Continuous and are never
                // touched by CompositeReplayExecutor (which only sets the parent's Mode). Without this the
                // optimized composite rebuild would run members in Continuous and fire their side effects
                // (RaiseSideEffects -> PublishMessage/AppendEvent), unlike the classic single-stream rebuild.
                // Propagating the composite's Mode keeps members in CatchUp/Rebuild during a rebuild
                // (side effects suppressed) and in Continuous during genuine continuous operation.
                foreach (var execution in stage.Executions)
                {
                    execution.Mode = Mode;
                }

                await stage.ExecuteDownstreamAsync(range, keptThrough);
            }

            // Compact each child execution's aggregate caches now that every stage has run.
            // BuildBatchAsync skipped per-stage compaction because downstream stages need to
            // read the upstream's in-flight entities; we do it once here at the composite
            // boundary instead. See JasperFx/marten#4329.
            foreach (var stage in _inners)
            {
                foreach (var execution in stage.Executions)
                {
                    await execution.CompactCachesAsync();
                }
            }

            return batch;
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Subscription {Name} failed while creating a SQL batch for updates for events from {Floor} to {Ceiling}",
                _shardName.Identity, range.SequenceFloor, range.SequenceCeiling);

            if (batch != null)
            {
                await batch!.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}
