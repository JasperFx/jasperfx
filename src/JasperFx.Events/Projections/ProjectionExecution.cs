using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.NewStuff;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace JasperFx.Events.Projections;

internal class ProjectionExecution<TOperations, TQuerySession> : ISubscriptionExecution where TOperations : TQuerySession, IStorageOperations
{
    private readonly ShardName _shardName;
    private readonly IEventStorage<TOperations, TQuerySession> _storage;
    private readonly IEventDatabase _database;
    private readonly EventProjection<TOperations, TQuerySession> _projection;
    private readonly ILogger _logger;
    private readonly ActionBlock<EventRange> _building;
    private readonly CancellationTokenSource _cancellation = new();

    public ProjectionExecution(ShardName shardName, IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, EventProjection<TOperations, TQuerySession> projection, ILogger logger)
    {
        _shardName = shardName;
        _storage = storage;
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
            var options = _storage.ErrorHandlingOptions(Mode);

            await using var batch = options.SkipApplyErrors
                ? await buildBatchWithSkipping(range, _cancellation.Token).ConfigureAwait(false)
                : await buildBatchAsync(range, _cancellation.Token).ConfigureAwait(false);

            // Executing the SQL commands for the ProjectionUpdateBatch
            await applyBatchOperationsToDatabaseAsync(range, batch).ConfigureAwait(false);

            range.Agent.Metrics.UpdateProcessed(range.Size);
        }
        catch (Exception e)
        {
            activity?.RecordException(e);
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
        IProjectionBatch batch = default;
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

        return batch;
    }

    private async Task<IProjectionBatch> buildBatchAsync(EventRange range, CancellationToken cancellationToken)
    {
        IProjectionBatch<TOperations, TQuerySession> batch = default;
        try
        {
            // TODO -- the projection batch wrapper will really need to know how to dispose all sessions built
            batch = await _storage.StartProjectionBatchAsync(range, _database, Mode, cancellationToken);

            var groups = range.Events.GroupBy(x => x.TenantId).ToArray();
            foreach (var group in groups)
            {
                await using var session = batch.SessionForTenant(group.Key);
                foreach (var e in group)
                {
                    try
                    {
                        await _projection.ApplyAsync(session, e, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // check if is transient, and if now, throw ApplyEventException
                        throw;  
                    }
                }
            }

        }
        catch (Exception e)
        {
            // TODO -- watch this carefully!!!! This will be errors from trying to apply events
            // you might get transient errors even after the retries
            // More likely, this might be a collection of ApplyEventException, and thus, retry the batch w/ skipped
            // sequences

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