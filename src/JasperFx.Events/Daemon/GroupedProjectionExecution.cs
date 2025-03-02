using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace JasperFx.Events.Daemon;

public class GroupedProjectionExecution: ISubscriptionExecution
{
    private readonly ActionBlock<EventRange> _building;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TransformBlock<EventRange, EventRange> _grouping;
    private readonly ILogger _logger;
    private readonly IGroupedProjectionRunner _runner;

    public GroupedProjectionExecution(IGroupedProjectionRunner runner, ILogger logger)
    {
        _logger = logger;

        var singleFileOptions = _cancellation.Token.SequentialOptions();
        _grouping = new TransformBlock<EventRange, EventRange>(groupEventRange, singleFileOptions);
        _building = new ActionBlock<EventRange>(processRange, singleFileOptions);
        _grouping.LinkTo(_building, x => x != null);

        _runner = runner;
    }

    public string ProjectionShardIdentity => _runner.ProjectionShardIdentity;

    public string ShardIdentity => _runner.ShardIdentity;

    public ShardExecutionMode Mode { get; set; }

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        return _runner.TryBuildReplayExecutor(out executor);
    }

    public string DatabaseName => _runner.DatabaseIdentifier;

    public Task EnsureStorageExists()
    {
        return _runner.EnsureStorageExists(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync().ConfigureAwait(false);
#else
        _cancellation.Cancel();
#endif

        _grouping.Complete();
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

        _grouping.Post(range);
    }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        _grouping.Complete();
        await _grouping.Completion.ConfigureAwait(false);
        _building.Complete();
        await _building.Completion.ConfigureAwait(false);

#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync().ConfigureAwait(false);
#else
        _cancellation.Cancel();
#endif
    }

    public async Task HardStopAsync()
    {
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync().ConfigureAwait(false);
#else
        _cancellation.Cancel();
#endif
        _grouping.Complete();
        _building.Complete();
    }

    private async Task<EventRange> groupEventRange(EventRange range)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return null;
        }

        using var activity = range.Agent.Metrics.TrackGrouping(range);

        try
        {
            if (_runner.SliceBehavior == SliceBehavior.Preprocess)
            {
                await range.SliceAsync(_runner.Slicer);
                
                if (_logger.IsEnabled(LogLevel.Debug) && Mode == ShardExecutionMode.Continuous)
                {
                    _logger.LogDebug(
                        "Subscription {Name} successfully grouped {Number} events with a floor of {Floor} and ceiling of {Ceiling}",
                        ProjectionShardIdentity, range.Events.Count, range.SequenceFloor, range.SequenceCeiling);
                }
            }

            return range;
        }
        catch (Exception e)
        {
            activity?.RecordException(e);
            _logger.LogError(e, "Failure trying to group events for {Name} from {Floor} to {Ceiling}",
                ProjectionShardIdentity, range.SequenceFloor, range.SequenceCeiling);
            await range.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);

            return null;
        }
        finally
        {
            activity?.Stop();
        }
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
            var options = _runner.ErrorHandlingOptions(Mode);

            await using var batch = options.SkipApplyErrors
                ? await buildBatchWithSkipping(range, _cancellation.Token).ConfigureAwait(false)
                : await buildBatchAsync(range).ConfigureAwait(false);

            // Executing the SQL commands for the ProjectionUpdateBatch
            await applyBatchOperationsToDatabaseAsync(range, batch).ConfigureAwait(false);

            range.Agent.Metrics.UpdateProcessed(range.Size);
        }
        catch (Exception e)
        {
            activity?.RecordException(e);
            _logger.LogError(e,
                "Error trying to build and apply changes to event subscription {Name} from {Floor} to {Ceiling}",
                ProjectionShardIdentity, range.SequenceFloor, range.SequenceCeiling);
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
                _logger.LogInformation("Shard '{ProjectionShardIdentity}': Executed updates for {Range}",
                    ProjectionShardIdentity, range);
            }
        }
        catch (Exception e)
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _logger.LogError(e,
                    "Failure in shard '{ProjectionShardIdentity}' trying to execute an update batch for {Range}",
                    ProjectionShardIdentity,
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
                batch = await buildBatchAsync(range).ConfigureAwait(false);
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

    private async Task<IProjectionBatch> buildBatchAsync(EventRange range)
    {
        IProjectionBatch batch = default;
        try
        {
            batch = await _runner.BuildBatchAsync(range).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // TODO -- watch this carefully!!!! This will be errors from trying to apply events
            // you might get transient errors even after the retries
            // More likely, this might be a collection of ApplyEventException, and thus, retry the batch w/ skipped
            // sequences

            _logger.LogError(e,
                "Subscription {Name} failed while creating a SQL batch for updates for events from {Floor} to {Ceiling}",
                ProjectionShardIdentity, range.SequenceFloor, range.SequenceCeiling);

            if (batch != null)
            {
                await batch!.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return batch;
    }
}
