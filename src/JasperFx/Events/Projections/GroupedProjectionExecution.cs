using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Grouping;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace JasperFx.Events.Projections;



public class GroupedProjectionExecution<TBatch, TGroup>: ISubscriptionExecution
    where TGroup : EventRangeGroup<TBatch>
    where TBatch : IProjectionBatch
{
    private readonly ActionBlock<TGroup> _building;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TransformBlock<EventRange, TGroup> _grouping;
    private readonly ILogger _logger;
    private readonly IGroupedProjectionRunner<TBatch, TGroup> _runner;

    public GroupedProjectionExecution(IGroupedProjectionRunner<TBatch, TGroup> runner, ILogger logger)
    {
        _logger = logger;

        var singleFileOptions = _cancellation.Token.SequentialOptions();
        _grouping = new TransformBlock<EventRange, TGroup>(groupEventRange, singleFileOptions);
        _building = new ActionBlock<TGroup>(processRange, singleFileOptions);
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

        var range = new EventRange(subscriptionAgent.Name, page.Floor, page.Ceiling)
        {
            Agent = subscriptionAgent, Events = page
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

    private async Task<TGroup> groupEventRange(EventRange range)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return null;
        }

        using var activity = range.Agent.Metrics.TrackGrouping(range);

        try
        {
            var group = await _runner.GroupEvents(range, _cancellation.Token).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Debug) && Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogDebug(
                    "Subscription {Name} successfully grouped {Number} events with a floor of {Floor} and ceiling of {Ceiling}",
                    ProjectionShardIdentity, range.Events.Count, range.SequenceFloor, range.SequenceCeiling);
            }

            return group;
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

    private async Task processRange(TGroup group)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        using var activity = group.Range.Agent.Metrics.TrackExecution(group.Range);

        try
        {
            // This should be done *once* before proceeding
            // And this cannot be put inside of ConfigureUpdateBatch
            // Low chance of errors
            group.Reset();

            var options = _runner.ErrorHandlingOptions(Mode);

            await using var batch = options.SkipApplyErrors
                ? await buildBatchWithSkipping(group, _cancellation.Token).ConfigureAwait(false)
                : await buildBatchAsync(group).ConfigureAwait(false);

            // Executing the SQL commands for the ProjectionUpdateBatch
            await applyBatchOperationsToDatabaseAsync(group, batch).ConfigureAwait(false);

            group.Agent.Metrics.UpdateProcessed(group.Range.Size);
        }
        catch (Exception e)
        {
            activity?.RecordException(e);
            _logger.LogError(e,
                "Error trying to build and apply changes to event subscription {Name} from {Floor} to {Ceiling}",
                ProjectionShardIdentity, group.Range.SequenceFloor, group.Range.SequenceCeiling);
            await group.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async Task applyBatchOperationsToDatabaseAsync(TGroup group,
        TBatch batch)
    {
        try
        {
            // Polly is already around the basic retry here, so anything that gets past this
            // probably deserves a full circuit break
            await batch.ExecuteAsync(_cancellation.Token).ConfigureAwait(false);

            group.Agent.MarkSuccess(group.Range.SequenceCeiling);

            if (Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogInformation("Shard '{ProjectionShardIdentity}': Executed updates for {Range}",
                    ProjectionShardIdentity, group.Range);
            }
        }
        catch (Exception e)
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _logger.LogError(e,
                    "Failure in shard '{ProjectionShardIdentity}' trying to execute an update batch for {Range}",
                    ProjectionShardIdentity,
                    group.Range);
                throw;
            }
        }
        finally
        {
            await batch.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<TBatch> buildBatchWithSkipping(TGroup group,
        CancellationToken cancellationToken)
    {
        TBatch batch = default;
        while (batch == null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                batch = await buildBatchAsync(group).ConfigureAwait(false);
            }
            catch (ApplyEventException e)
            {
                await group.SkipEventSequence(e.Event.Sequence).ConfigureAwait(false);
                await group.Agent.RecordDeadLetterEventAsync(new DeadLetterEvent(e.Event, group.Range.ShardName, e))
                    .ConfigureAwait(false);
            }
        }

        return batch;
    }

    private async Task<TBatch> buildBatchAsync(TGroup group)
    {
        TBatch batch = default;
        try
        {
            batch = await _runner.BuildBatchAsync(group).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // TODO -- watch this carefully!!!! This will be errors from trying to apply events
            // you might get transient errors even after the retries
            // More likely, this might be a collection of ApplyEventException, and thus, retry the batch w/ skipped
            // sequences

            _logger.LogError(e,
                "Subscription {Name} failed while creating a SQL batch for updates for events from {Floor} to {Ceiling}",
                ProjectionShardIdentity, group.Range.SequenceFloor, group.Range.SequenceCeiling);

            if (batch != null)
            {
                await batch!.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            // Clean up the group, release sessions. TODO -- find a way to eliminate this
            group.Dispose();
        }

        return batch;
    }
}
