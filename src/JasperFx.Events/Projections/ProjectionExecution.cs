using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

public class ProjectionExecution<TOperations, TQuerySession> : ISubscriptionExecution where TOperations : TQuerySession, IStorageOperations
{
    private readonly ShardName _shardName;
    private readonly AsyncOptions _options;
    private readonly IEventStore<TOperations, TQuerySession> _store;
    private readonly IEventDatabase _database;
    private readonly IJasperFxProjection<TOperations> _projection;
    private readonly ILogger _logger;
    private readonly ActionBlock<EventRange> _building;
    private readonly CancellationTokenSource _cancellation = new();

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
        
        var singleFileOptions = _cancellation.Token.SequentialOptions();
        _building = new ActionBlock<EventRange>(processRange, singleFileOptions);
    }
    
    private async Task processRange(EventRange range)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        using var activity = range.Agent.Metrics.TrackExecution(range);

        try
        {
            var options = _store.ErrorHandlingOptions(Mode);

            await using var batch = options.SkipApplyErrors
                ? await buildBatchWithSkipping(range, _cancellation.Token).ConfigureAwait(false)
                : await buildBatchAsync(range, _cancellation.Token).ConfigureAwait(false);

            // Executing the SQL commands for the ProjectionUpdateBatch
            await applyBatchOperationsToDatabaseAsync(range, batch).ConfigureAwait(false);

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

    private async Task applyBatchOperationsToDatabaseAsync(EventRange range, IProjectionBatch batch)
    {
        try
        {
            // Polly is already around the basic retry here, so anything that gets past this
            // probably deserves a full circuit break
            await batch.ExecuteAsync(_cancellation.Token).ConfigureAwait(false);

            range.Agent.MarkSuccess(range.SequenceCeiling);

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
                batch = await buildBatchAsync(range, cancellationToken).ConfigureAwait(false);
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

    private async Task<IProjectionBatch> buildBatchAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession>? batch = null;
        try
        {
            batch = await _store.StartProjectionBatchAsync(range, _database, Mode, _options, cancellationToken);

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

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        
        _building.Complete();
    }

    public void Enqueue(EventPage page, ISubscriptionAgent subscriptionAgent)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        var range = new EventRange(subscriptionAgent, page.Floor, page.Ceiling)
        {
            Events = page
        };

        _building.Post(range);
    }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        _building.Complete();
        await _building.Completion.ConfigureAwait(false);
        
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
}