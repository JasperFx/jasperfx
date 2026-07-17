using System.Diagnostics.CodeAnalysis;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public class GroupedProjectionExecution : ISubscriptionExecution
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IBlock<EventRange> _grouping;
    private readonly ILogger _logger;
    private readonly IGroupedProjectionRunner _runner;

    public GroupedProjectionExecution(ShardName shardName, IGroupedProjectionRunner runner, ILogger logger)
    {
        ShardName = shardName;
        _logger = logger;

        var block = new Block<EventRange>(processRangeAsync);
        block.OnError = onBlockFailure;
        _grouping = block.PushUpstream<EventRange>(groupEventRangeAsync);
        _grouping.OnError = onBlockFailure;

        _runner = runner;
    }

    // Last-resort sink for exceptions that escape processRangeAsync/groupEventRangeAsync (which
    // have their own error handling) or that fault the block itself. Without this, such failures
    // fell into Block<T>'s invisible default error handler and the shard died with zero log
    // output (jasperfx#506)
    private void onBlockFailure(EventRange? range, Exception ex)
    {
        _logger.LogError(ex, "Exception escaped the projection execution block for shard {Name}",
            ShardName.Identity);

        if (range?.Agent != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await range.Agent.ReportCriticalFailureAsync(ex).ConfigureAwait(false);
                }
                catch (Exception reportingException)
                {
                    _logger.LogError(reportingException,
                        "Failure while reporting a critical failure for shard {Name}", ShardName.Identity);
                }
            });
        }
    }

    public ShardName ShardName { get; }

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

        // jasperfx#525: drop any un-flushed deferred-rebuild writes. A rebuild that completed normally already
        // flushed its final window (that's what let it complete), so this is a no-op there; a rebuild torn down
        // early discards its accumulator so nothing partial is written and replay stays clean.
        _runner.DiscardDeferredRebuildWrites();

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

        // jasperfx#525: a hard stop mid-rebuild throws away the un-flushed accumulator (see DisposeAsync).
        _runner.DiscardDeferredRebuildWrites();
    }

    public async Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage page,
        CancellationToken cancellation)
    {
        var range = new EventRange(subscriptionAgent, page.Floor, page.Ceiling)
        {
            Events = page
        };

        await groupEventRangeAsync(range, cancellation);

        await processRangeAsync(range, cancellation);
    }
    
    public async Task ProcessRangeAsync(EventRange range)
    {
        await groupEventRangeAsync(range, CancellationToken.None);
        await processRangeAsync(range, CancellationToken.None);
    }

    public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
    {
        caching = _runner as IAggregateCaching<TId, TDoc>;
        return caching != null;
    }

    public Task CompactCachesAsync() => _runner.CompactCachesAsync();

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
                        ShardName.Identity, range.Events.Count, range.SequenceFloor, range.SequenceCeiling);
                }
            }

            return range;
        }
        catch (Exception e) when (_cancellation.IsCancellationRequested)
        {
            // Shard is being torn down — don't promote a cancellation side effect to a critical
            // failure. Anything that is NOT a genuine cancellation side effect is still logged
            // before the teardown discards it (jasperfx#507)
            if (!CancellationExceptions.IsCancellationLike(e))
            {
                _logger.LogInformation(e,
                    "Discarding a failure while grouping events for {Name} because the shard is being stopped, but the exception does not look like a cancellation side effect",
                    ShardName.Identity);
            }

            return null!;
        }
        catch (Exception e)
        {
            activity?.AddException(e);
            _logger.LogError(e, "Failure trying to group events for {Name} from {Floor} to {Ceiling}",
                ShardName.Identity, range.SequenceFloor, range.SequenceCeiling);
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

        IProjectionBatch batch = null;
        
        try
        {
            var options = _runner.ErrorHandlingOptions(Mode);

            // CANNOT dispose the batch with using declaration here in the case of Composite batches
            batch = options.SkipApplyErrors
                ? await buildBatchWithSkipping(range, _cancellation.Token).ConfigureAwait(false)
                : await buildBatchAsync(range, _cancellation.Token).ConfigureAwait(false);

            // Has to be the result of configuring apply event skipping *and*
            // hitting an error in skipping. Just get out of here. The Projection/Subscription
            // should be stopped in this case
            if (batch == null) return;

            if (range.BatchBehavior == BatchBehavior.Individual)
            {
                if (_runner.DefersRebuildWrites(Mode, range))
                {
                    // jasperfx#525: the per-slice writes were accumulated in the runner during BuildBatchAsync,
                    // so the batch built here carries no operations — dispose it (it was only used to load prior
                    // snapshots) and flush the accumulator into its own batch at the threshold or the rebuild
                    // ceiling, rather than committing per range. Progress advances only at those flushes.
                    await batch.DisposeAsync();

                    if (_runner.DeferredFlushDue(range))
                    {
                        await _runner
                            .FlushDeferredRebuildWritesAsync(range, range.SequenceCeiling, _cancellation.Token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // jasperfx#525: nothing flushed, so committed progression stays put — but tell the agent
                        // this range was buffered so its loader keeps pumping the next page instead of stalling.
                        await range.Agent.MarkRangeBufferedAsync(range.SequenceCeiling).ConfigureAwait(false);
                    }
                }
                else
                {
                    try
                    {
                        // Executing the SQL commands for the ProjectionUpdateBatch
                        await applyBatchOperationsToDatabaseAsync(range, batch).ConfigureAwait(false);
                    }
                    finally
                    {
                        await batch.DisposeAsync();
                    }
                }
            }

            range.Agent.Metrics.UpdateProcessed(range.Size);
        }
        catch (Exception e) when (_cancellation.IsCancellationRequested)
        {
            // Daemon-internal cancellation (StopAllAsync / HardStopAsync / DisposeAsync fired the
            // shard's CTS). A genuine cancellation side effect — OCE, wrapped/aggregated OCE, or a
            // database exception whose SqlState is query-cancelled/connection-teardown — isn't a
            // shard failure. Anything else (a schema problem is a schema problem regardless of the
            // CTS state) is logged before the teardown discards it (jasperfx#507)
            if (!CancellationExceptions.IsCancellationLike(e))
            {
                _logger.LogInformation(e,
                    "Discarding a failure while processing events for {Name} because the shard is being stopped, but the exception does not look like a cancellation side effect",
                    ShardName.Identity);
            }
        }
        catch (Exception e)
        {
            activity?.AddException(e);
            _logger.LogError(e,
                "Error trying to build and apply changes to event subscription {Name} from {Floor} to {Ceiling}",
                ShardName.Identity, range.SequenceFloor, range.SequenceCeiling);
            await range.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
        finally
        {
            if (range.BatchBehavior == BatchBehavior.Individual && batch is not null)
            {
                await batch.DisposeAsync();
            }
            
            activity?.Stop();
        }
    }

    private async Task applyBatchOperationsToDatabaseAsync(EventRange range, IProjectionBatch batch)
    {
        // Epic #486 WS3: bound concurrent batch execute/commit sessions per database. Only
        // BatchBehavior.Individual ranges reach this method (composite members ride the parent's
        // batch and never execute), so the slot economy cannot deadlock on nested batches.
        var writeThrottle = range.Agent.BatchWriteThrottle;
        if (writeThrottle != null)
        {
            try
            {
                await writeThrottle.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // Shard is being torn down while queued for a write slot — nothing was executed,
                // so don't mark success and don't treat the teardown as a failure
                await batch.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }

        try
        {
            // Polly is already around the basic retry here, so anything that gets past this
            // probably deserves a full circuit break
            await batch.ExecuteAsync(_cancellation.Token).ConfigureAwait(false);

            // #4730: the batch committed, so it is now safe to publish this build's aggregate-cache
            // mutations. A build/commit that failed never reaches here, so its mutations are
            // discarded and a retry rebuilds from committed state instead of double-applying.
            _runner.ApplyPendingCacheUpdates();

            await range.Agent.MarkSuccessAsync(range.SequenceCeiling);

            if (Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogInformation("Shard '{_shardName.Identity}': Executed updates for {Range}",
                    ShardName.Identity, range);
            }
        }
        catch (Exception e)
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _logger.LogError(e,
                    "Failure in shard '{_shardName.Identity}' trying to execute an update batch for {Range}",
                    ShardName.Identity,
                    range);
                throw;
            }

            if (!CancellationExceptions.IsCancellationLike(e))
            {
                _logger.LogInformation(e,
                    "Discarding a failure while executing an update batch for {Range} in shard '{Identity}' because the shard is being stopped, but the exception does not look like a cancellation side effect",
                    range, ShardName.Identity);
            }
        }
        finally
        {
            writeThrottle?.Release();
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
                ShardName.Identity, range.SequenceFloor, range.SequenceCeiling);

            if (batch != null)
            {
                await batch!.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return batch;
    }
}