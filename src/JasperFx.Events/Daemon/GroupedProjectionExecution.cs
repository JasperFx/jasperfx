using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public class GroupedProjectionExecution : ISubscriptionExecution
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IBlock<EventRange> _grouping;
    private readonly ILogger _logger;
    private readonly IGroupedProjectionRunner _runner;
    private readonly ShardName _shardName;

    public GroupedProjectionExecution(ShardName shardName, IGroupedProjectionRunner runner, ILogger logger)
    {
        _shardName = shardName;
        _logger = logger;

        var block = new Block<EventRange>(processRangeAsync);
        _grouping = block.PushUpstream<EventRange>(groupEventRangeAsync);

        _runner = runner;
    }

    public object[]? Disposables { get; init; }

    public ShardExecutionMode Mode { get; set; }

    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        return _runner.TryBuildReplayExecutor(out executor);
    }

    public async ValueTask DisposeAsync()
    {
        _grouping.Complete();
        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (Disposables != null)
        {
            await Disposables.MaybeDisposeAllAsync().ConfigureAwait(false);
        }
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

        return _grouping.PostAsync(range);
    }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        _grouping.Complete();
        await _grouping.WaitForCompletionAsync().ConfigureAwait(false);

        await _cancellation.CancelAsync().ConfigureAwait(false);
    }

    public async Task HardStopAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _grouping.Complete();
    }

    private async Task<EventRange> groupEventRangeAsync(EventRange range, CancellationToken _)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return null!;
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
                        _shardName.Identity, range.Events.Count, range.SequenceFloor, range.SequenceCeiling);
                }
            }

            return range;
        }
        catch (Exception e)
        {
            activity?.AddException(e);
            _logger.LogError(e, "Failure trying to group events for {Name} from {Floor} to {Ceiling}",
                _shardName.Identity, range.SequenceFloor, range.SequenceCeiling);
            await range.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);

            return null!;
        }
        finally
        {
            activity?.Stop();
        }
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
            var options = _runner.ErrorHandlingOptions(Mode);

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
        IProjectionBatch? batch = default;
        while (batch == null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                batch = await buildBatchAsync(range, cancellationToken).ConfigureAwait(false);
            }
            catch (AggregateException a)
            {
                var applyErrors = a.InnerExceptions.OfType<ApplyEventException>().ToArray();
                foreach (var error in applyErrors)
                {
                    await range.SkipEventSequence(error.Event.Sequence).ConfigureAwait(false);
                    await range.Agent
                        .RecordDeadLetterEventAsync(new DeadLetterEvent(error.Event, range.ShardName, error))
                        .ConfigureAwait(false);
                }
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
        IProjectionBatch? batch = null;
        try
        {
            batch = await _runner.BuildBatchAsync(range, Mode, cancellationToken).ConfigureAwait(false);
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