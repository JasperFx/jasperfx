using System.Diagnostics.CodeAnalysis;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

public class ProjectionExecution<TOperations, TQuerySession> : ISubscriptionExecution
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Block<EventRange> _building;
    protected readonly CancellationTokenSource _cancellation = new();
    protected readonly IEventDatabase _database;
    protected readonly ILogger _logger;
    protected readonly AsyncOptions _options;
    private readonly IJasperFxProjection<TOperations> _projection;
    protected readonly ShardName _shardName;
    protected readonly IEventStore<TOperations, TQuerySession> _store;

    public ProjectionExecution(ShardName shardName, AsyncOptions options,
        IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        IJasperFxProjection<TOperations> projection, ILogger logger)
    {
        _shardName = shardName;
        _options = options;
        _store = store;
        _database = database;
        _projection = projection;
        _logger = logger;

        _building = new Block<EventRange>(processRangeAsync);
    }

    public ShardName ShardName => _shardName;

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        _building.Complete();
    }

    public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return new ValueTask();
        }

        var range = new EventRange(subscriptionAgent, page.Floor, page.Ceiling)
        {
            Events = page
        };

        return _building.PostAsync(range);
    }

    public async Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage page, CancellationToken cancellation)
    {
        var range = new EventRange(subscriptionAgent, page.Floor, page.Ceiling)
        {
            Events = page
        };

        await processRangeAsync(range, cancellation);
    }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        await _building.WaitForCompletionAsync();
        await _cancellation.CancelAsync().ConfigureAwait(false);
    }

    public async Task HardStopAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _building.Complete();
    }

    public ShardExecutionMode Mode { get; set; }

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        executor = default!;
        return false;
    }

    public Task ProcessRangeAsync(EventRange range)
    {
        return processRangeAsync(range, CancellationToken.None);
    }

    bool ISubscriptionExecution.TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
    {
        caching = null;
        return false;
    }

    private async Task processRangeAsync(EventRange range, CancellationToken _)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        using var activity = range.Agent.Metrics.TrackExecution(range);

        try
        {
            await using var batch = await buildBatchAsync(range);

            // Executing the SQL commands for the ProjectionUpdateBatch
            if (range.BatchBehavior == BatchBehavior.Individual)
            {
                await applyBatchOperationsToDatabaseAsync(range, batch).ConfigureAwait(false);
            }

            range.Agent.Metrics.UpdateProcessed(range.Size);
        }
        catch (Exception e)
        {
            activity?.AddException(e);
            _logger.LogError(e,
                "Error trying to build and apply changes to event subscription {Name} from {Floor} to {Ceiling}",
                _shardName.Identity, range.SequenceFloor, range.SequenceCeiling);
            await range.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
        finally
        {
            activity?.Stop();
        }
    }

    protected virtual async Task<IProjectionBatch> buildBatchAsync(EventRange range)
    {
        IProjectionBatch? batch = null;
        try
        {
            var options = _store.ErrorHandlingOptions(Mode);

            batch = options.SkipApplyErrors
                ? await buildBatchWithSkipping(range, _cancellation.Token).ConfigureAwait(false)
                : await buildBatchWithNoSkippingAsync(range, _cancellation.Token).ConfigureAwait(false);
            return batch;
        }
        catch
        {
            await batch.DisposeAsync();
            throw;
        }
    }

    private async Task applyBatchOperationsToDatabaseAsync(EventRange range, IProjectionBatch batch)
    {
        try
        {
            // Polly is already around the basic retry here, so anything that gets past this
            // probably deserves a full circuit break
            await batch.ExecuteAsync(_cancellation.Token).ConfigureAwait(false);

            await range.Agent.MarkSuccessAsync(range.SequenceCeiling);

            if (Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogInformation("Shard '{_shardName.Identity}': Executed updates for {Range}",
                    _shardName.Identity, range);
            }
        }
        catch (Exception e)
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _logger.LogError(e,
                    "Failure in shard '{_shardName.Identity}' trying to execute an update batch for {Range}",
                    _shardName.Identity,
                    range);
                throw;
            }
        }
        finally
        {
            await batch.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IProjectionBatch> buildBatchWithSkipping(EventRange range,
        CancellationToken cancellationToken)
    {
        IProjectionBatch? batch = null;
        while (batch == null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                batch = await buildBatchWithNoSkippingAsync(range, cancellationToken).ConfigureAwait(false);
            }
            catch (ApplyEventException e)
            {
                await range.SkipEventSequence(e.Event.Sequence).ConfigureAwait(false);
                await range.Agent.RecordDeadLetterEventAsync(new DeadLetterEvent(e.Event, range.ShardName, e))
                    .ConfigureAwait(false);
            }
        }

        return batch!;
    }

    protected virtual async Task<IProjectionBatch> buildBatchWithNoSkippingAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession>? batch = null;
        try
        {
            batch = range.ActiveBatch as IProjectionBatch<TOperations, TQuerySession> ??
                    await _store.StartProjectionBatchAsync(range, _database, Mode, _options, cancellationToken);

            var groups = range.Events.GroupBy(x => x.TenantId).ToArray();
            foreach (var group in groups)
            {
                await using var session = batch.SessionForTenant(group.Key);
                await _projection.ApplyAsync(session, group.ToList(), cancellationToken);
            }
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

        return batch;
    }
}