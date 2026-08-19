using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public partial class SubscriptionAgent : ISubscriptionAgent, IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Block<Command> _commandBlock;
    private readonly ISubscriptionExecution _execution;
    private readonly IEventLoader _loader;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ShardStateTracker _tracker;

    private TaskCompletionSource? _rebuild;
    private IDaemonRuntime _runtime = new NulloDaemonRuntime();
    private Timer? _heartbeatTimer;

    // #4721: true while a continuous-startup optimized rebuild runs OFF the command-loop consumer.
    // Only ever read/written on the command-loop thread (Apply), so it needs no synchronization.
    // While set, the command loop keeps draining RangeCompleted/HighWater bookkeeping but does NOT
    // start continuous loading, so the rebuild's MarkSuccessAsync posts cannot deadlock the bounded
    // command channel and continuous loading cannot race the rebuild over the same events.
    private bool _replaying;
    private Task? _replayTask;

    // jasperfx#598/#610: blue/green side-effect gate state. _sideEffectGateMark is the highest PRIOR
    // version's persisted progression (0 = no gate); while committed progression sits below it this
    // agent runs normally but suppresses side effects, and clamps its loading ceiling to the mark so
    // no page ever straddles it. Both are only WRITTEN on the command-loop thread; the suppression
    // flag is volatile because the executions read it from their own threads through
    // EventRange.Agent.SideEffectsSuppressed.
    private long _sideEffectGateMark;
    private volatile bool _sideEffectsSuppressed;

    // jasperfx#598/#610: the daemon's warm-up governor and this agent's standing on it.
    // 0 = no slot wanted or held, 1 = a waiter is queued, 2 = a slot is held. Interlocked rather than
    // command-loop-only because the release has to be safe from DisposeAsync racing the waiter.
    private SemaphoreSlim? _warmupThrottle;
    private int _warmupSlot;

    public SubscriptionAgent(ShardName name, AsyncOptions options, TimeProvider timeProvider, IEventLoader loader,
        ISubscriptionExecution execution, ShardStateTracker tracker, ISubscriptionMetrics metrics, ILogger logger)
    {
        Options = options;
        _timeProvider = timeProvider;
        _loader = loader;
        _execution = execution;
        _tracker = tracker;
        Metrics = metrics;
        _logger = logger;
        Name = name;

        _commandBlock = new Block<Command>(Apply);

        ProjectionShardIdentity = name.Identity;

        // Last-resort sink for exceptions escaping the command loop. Without this, a failed
        // command fell into Block<T>'s invisible default error handler and the agent silently
        // stopped making progress (jasperfx#506). ReportCriticalFailureAsync does not post back
        // onto the command block, so there is no recursion here
        _commandBlock.OnError = (command, ex) =>
        {
            _logger.LogError(ex, "Error processing daemon command {Command} for shard {Identity}",
                command, ProjectionShardIdentity);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ReportCriticalFailureAsync(ex).ConfigureAwait(false);
                }
                catch (Exception reportingException)
                {
                    _logger.LogError(reportingException,
                        "Failure while reporting a critical failure for shard {Identity}",
                        ProjectionShardIdentity);
                }
            });
        };
    }

    // Exposed so tests can assert how the daemon composed this agent's loader (jasperfx#494)
    internal IEventLoader Loader => _loader;

    // Epic #486 WS3: surface the owning daemon's batch-write governor to the projection
    // executions (they only see this agent via EventRange.Agent). The runtime is the daemon
    // once StartAsync/ReplayAsync has run; the Nullo default yields null = unbounded.
    public SemaphoreSlim? BatchWriteThrottle => _runtime.BatchWriteThrottle;

    public string ProjectionShardIdentity { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    // Making the setter internal so the test harness can override it
    // It's naughty, will make some people get very upset, and
    // makes unit testing much simpler. I'm not ashamed
    public long LastEnqueued { get; set; }

    public long LastCommitted { get; set; }

    public long HighWaterMark { get; set; }

    // jasperfx#525: highest range ceiling a deferred rebuild has BUFFERED (accumulated in memory) but not yet
    // committed. Kept >= LastCommitted at all times. Loading back-pressure is measured against this rather than
    // LastCommitted so a deferred rebuild — whose LastCommitted intentionally lags until the next flush — keeps
    // pumping pages instead of stalling. Only touched on the command-loop thread.
    private long _bufferedCeiling;

    // Volatile: the warm-up slot waiter (jasperfx#598) runs off the command loop and reads this to
    // decide whether the slot it just won still has an owner.
    private volatile bool _disposed;

    public async ValueTask DisposeAsync()
    {
        // jasperfx#540: be idempotent. A faulted start is now torn down at the point of failure (see
        // JasperFxAsyncDaemon.tryStartAgentAsync), and the daemon's start caller ALSO disposes the agent
        // on a false return -- so the same agent can legitimately be disposed twice. Guard so the second
        // call is a harmless no-op rather than re-cancelling/re-completing/re-disposing the execution.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // jasperfx#598: hand back the warm-up slot before anything else, so a shard torn down mid
        // warm-up cannot starve the next one out of the throttle for the daemon's lifetime.
        releaseWarmupSlot();

        if (_heartbeatTimer != null)
        {
            await _heartbeatTimer.DisposeAsync().ConfigureAwait(false);
            _heartbeatTimer = null;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        _commandBlock.Complete();
        await _execution.DisposeAsync().ConfigureAwait(false);
    }

    public ShardName Name { get; }

    public AsyncOptions Options { get; }

    public ErrorHandlingOptions ErrorOptions { get; private set; } = new();

    public async Task ReportCriticalFailureAsync(Exception ex, long lastProcessed)
    {
        await ReportCriticalFailureAsync(ex).ConfigureAwait(false);
        await MarkSuccessAsync(lastProcessed);
    }

    public async Task ReportCriticalFailureAsync(Exception ex)
    {
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            await _execution.HardStopAsync().ConfigureAwait(false);

            // jasperfx#565: every failure path in the daemon funnels through here, so this is the one
            // place the reason gets classified. Published on the ShardState AND held on the agent, because
            // the two consumers differ: the tracker feeds observers and the persisted extended progression
            // row, while an external supervisor (Wolverine's EventSubscriptionAgent) polls the live agent.
            // PauseReason keeps carrying the full exception text it always did.
            var failure = ShardFailure.For(ex, _timeProvider.GetUtcNow());
            Failure = failure;

            if (ex is ProgressionProgressOutOfOrderException)
            {
                PausedTime = null;
                Status = AgentStatus.Stopped;
                await _tracker.PublishAsync(stamp(new ShardState(Name, LastCommitted)
                {
                    Action = ShardAction.Stopped,
                    Exception = ex,
                    AgentStatus = "Stopped",
                    PauseReason = failure.Detail,
                    Failure = failure,
                    LastHeartbeat = _timeProvider.GetUtcNow()
                }));
            }
            else
            {
                PausedTime = _timeProvider.GetUtcNow();
                Status = AgentStatus.Paused;
                await _tracker.PublishAsync(stamp(new ShardState(Name, LastCommitted)
                {
                    Action = ShardAction.Paused,
                    Exception = ex,
                    AgentStatus = "Paused",
                    PauseReason = failure.Detail,
                    Failure = failure,
                    LastHeartbeat = _timeProvider.GetUtcNow()
                }));
            }

            if (Mode == ShardExecutionMode.Rebuild)
            {
                _rebuild?.SetException(ex);
            }

            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to pause subscription agent {Name}", ProjectionShardIdentity);
        }
    }

    long ISubscriptionAgent.Position => LastCommitted;

    public AgentStatus Status { get; private set; } = AgentStatus.Running;

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        try
        {
            // Let the command block finish first
            _commandBlock.Complete();

            await _cancellation.CancelAsync().ConfigureAwait(false);

            await _execution.StopAndDrainAsync(token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Just get out of here.
        }
        catch (OperationCanceledException)
        {
            // Nothing, just from shutting down
        }
        catch (Exception e)
        {
            throw new ShardStopException(ProjectionShardIdentity, e);
        }
        finally
        {
            // jasperfx#598: a graceful stop does NOT dispose the agent (the daemon just drops it from its
            // registry), so this is the only place a shard stopped mid warm-up can hand its throttle slot
            // back. Without it, one stopped shard starves the warm-up throttle for the daemon's lifetime.
            releaseWarmupSlot();

            _logger.LogInformation("Stopped projection agent {Name}", ProjectionShardIdentity);
            Status = AgentStatus.Stopped;
            await _tracker.PublishAsync(stamp(new ShardState(Name, LastCommitted)
            {
                Action = ShardAction.Stopped,
                AgentStatus = "Stopped",
                LastHeartbeat = _timeProvider.GetUtcNow()
            }));
        }
    }

    public async Task HardStopAsync()
    {
        await _execution.HardStopAsync().ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
        await _tracker.PublishAsync(stamp(new ShardState(Name, LastCommitted)
        {
            Action = ShardAction.Stopped,
            AgentStatus = "Stopped",
            LastHeartbeat = _timeProvider.GetUtcNow()
        }));
    }

    public async Task StartAsync(SubscriptionExecutionRequest request)
    {
        Mode = request.Mode;
        _execution.Mode = request.Mode;
        ErrorOptions = request.ErrorHandling;
        _runtime = request.Runtime;

        // jasperfx#565: a fresh start supersedes whatever paused this agent last. Clearing it here (rather
        // than leaving the last reason hanging around) is what keeps a supervisor from alerting on a
        // failure the operator already recovered from.
        Failure = null;

        // jasperfx#598/#610: arm the blue/green side-effect gate BEFORE the Start command is posted, so the
        // very first page the command loop loads is already clamped to the gate mark and already suppressed.
        // Only meaningful for a Continuous start: a Rebuild suppresses side effects wholesale anyway.
        if (request.Mode == ShardExecutionMode.Continuous && request.SideEffectGateMark > request.Floor)
        {
            _sideEffectGateMark = request.SideEffectGateMark;
            _sideEffectsSuppressed = true;
            _warmupThrottle = request.Runtime.SideEffectGateWarmupThrottle;

            _logger.LogInformation(
                "Projection agent {Name} is starting inside the blue/green side-effect gate: it runs from {Floor} with side effects SUPPRESSED and enables them on reaching the prior version's mark of {Mark}",
                ProjectionShardIdentity, request.Floor, _sideEffectGateMark);
        }

        await _commandBlock.PostAsync(Command.Started(request.StartingHighWater ?? _tracker.HighWaterMark, request.Floor));
        await _tracker.PublishAsync(stamp(new ShardState(Name, request.Floor)
        {
            Action = ShardAction.Started,
            AgentStatus = "Running",
            LastHeartbeat = _timeProvider.GetUtcNow()
        }));

        startHeartbeatTimer();

        _logger.LogInformation("Started projection agent {Name}", ProjectionShardIdentity);
    }

    /// <summary>
    /// jasperfx#598/#610: true while this agent is catching up over events the PRIOR version of the
    /// projection already processed, so <c>RaiseSideEffects</c> must not fire for them. Read by the
    /// executions off their own threads via <see cref="EventRange.Agent"/>.
    /// </summary>
    public bool SideEffectsSuppressed => _sideEffectsSuppressed;

    // Stamp what this agent knows about itself onto every state it publishes. EVERY publish in this
    // class goes through here — a publish that skips it reports the agent's defaults rather than its
    // actual condition, which is exactly the bug jasperfx#681 was.
    private ShardState stamp(ShardState state)
    {
        // jasperfx#681: ShardMode.rebuilding had no writer anywhere in the tree, so every state this
        // agent published claimed `continuous` — including the ones published during a replay. The
        // agent already knows better; it just knows it on a different enum. Only Rebuild maps to
        // rebuilding: CatchUp is a shard catching up under normal operation, and telling those two
        // apart is the whole point of the distinction.
        state.Mode = Mode == ShardExecutionMode.Rebuild ? ShardMode.rebuilding : ShardMode.continuous;

        // jasperfx#598/#610: the warm-up window, so an operator watching the shard can tell "running,
        // but not emitting side effects yet" from "running normally" — the distinction that was
        // invisible while the warm-up hid inside the start path.
        //
        // Mark first, flag second: completeSideEffectGateAsync clears the flag before zeroing the mark, so
        // a publisher on another thread (MarkSuccessAsync, the heartbeat timer) racing the flip either
        // stamps a consistent pair or stamps nothing — never "suppressed, gated on 0".
        var mark = Interlocked.Read(ref _sideEffectGateMark);
        if (mark > 0 && _sideEffectsSuppressed)
        {
            state.SideEffectsSuppressed = true;
            state.SideEffectGateMark = mark;
        }

        return state;
    }

    // jasperfx#598/#610: the ceiling this agent may load up to. Inside the gate's warm-up window that is
    // the gate mark rather than the real high water, so no page ever STRADDLES the mark — a straddling
    // page would force a choice between re-emitting side effects the prior version already emitted and
    // dropping the ones only this version owes. The real high water still drives the reported gap.
    private long loadingCeiling()
    {
        var mark = Interlocked.Read(ref _sideEffectGateMark);
        return mark > 0 ? Math.Min(HighWaterMark, mark) : HighWaterMark;
    }

    // jasperfx#598/#610: committed progression has reached the prior version's mark, so everything from
    // here on is history the prior version never covered. Enable side effects, give the warm-up slot back,
    // and say so on the tracker and in the log. Runs on the command-loop thread only.
    private async Task completeSideEffectGateAsync()
    {
        _sideEffectsSuppressed = false;
        var mark = Interlocked.Exchange(ref _sideEffectGateMark, 0);
        releaseWarmupSlot();

        _logger.LogInformation(
            "Projection agent {Name} finished its side-effect-suppressed warm-up at {Mark}; side effects are enabled from here on",
            ProjectionShardIdentity, mark);

        await _tracker.PublishAsync(stamp(new ShardState(Name, LastCommitted)
        {
            Action = ShardAction.Updated,
            AgentStatus = Status.ToString(),
            LastHeartbeat = _timeProvider.GetUtcNow()
        }));
    }

    // jasperfx#598/#610: admission to the daemon's warm-up throttle. Returns true when this agent may
    // load — either the throttle is unset (unbounded) or a slot is already held. Otherwise it queues a
    // waiter OFF the command loop and returns false; the loop resumes on Command.WarmupSlotGranted.
    // Waiting inline would wedge this agent's high-water and completion bookkeeping for the whole queue
    // time, which at hundreds of gated shards is exactly the stall this issue exists to remove.
    private bool tryEnterWarmup()
    {
        var throttle = _warmupThrottle;
        if (throttle == null)
        {
            return true;
        }

        var standing = Interlocked.CompareExchange(ref _warmupSlot, 1, 0);
        if (standing == 2) return true;   // already holding a slot
        if (standing == 1) return false;  // a waiter is already queued

        _ = Task.Run(() => awaitWarmupSlotAsync(throttle));
        return false;
    }

    private async Task awaitWarmupSlotAsync(SemaphoreSlim throttle)
    {
        try
        {
            await throttle.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Torn down while queued. A cancelled wait never took a permit, and the standing was
            // already reset to 0 by DisposeAsync's release, so there is nothing to hand back.
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Interlocked.Exchange(ref _warmupSlot, 2);

        // The agent may have been disposed between the permit being granted and this line. Check AFTER
        // publishing the standing so the release below and DisposeAsync's release cannot both fire:
        // whichever runs second sees a standing of 0 from the other's Interlocked.Exchange.
        if (_disposed || _cancellation.IsCancellationRequested)
        {
            releaseWarmupSlot();
            return;
        }

        try
        {
            await _commandBlock.PostAsync(Command.WarmupSlotGranted()).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The grant will never be consumed (the block is completed, the agent is going away), so
            // the slot would sit held forever. Hand it straight back.
            _logger.LogDebug(e, "Could not deliver a side-effect gate warm-up slot to {Name}; releasing it",
                ProjectionShardIdentity);
            releaseWarmupSlot();
        }
    }

    private void releaseWarmupSlot()
    {
        if (Interlocked.Exchange(ref _warmupSlot, 0) == 2)
        {
            _warmupThrottle.SafeRelease();
        }
    }

    public async Task ReplayAsync(SubscriptionExecutionRequest request, long highWaterMark, TimeSpan timeout)
    {
        Mode = ShardExecutionMode.Rebuild;
        _rebuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _execution.Mode = ShardExecutionMode.Rebuild;
        ErrorOptions = request.ErrorHandling;
        _runtime = request.Runtime;
        Failure = null; // jasperfx#565: see StartAsync
        LastCommitted = request.Floor; // Force it to start here!
        _bufferedCeiling = request.Floor; // jasperfx#525

        try
        {
            if (!request.DisableOptimizedReplay && _execution.TryBuildReplayExecutor(out var executor))
            {
                _logger.LogInformation("Starting optimized rebuild for projection/subscription {ShardName}",
                    Name.Identity);
                var cancellationSource = new CancellationTokenSource(timeout);
                await executor.StartAsync(request, this, cancellationSource.Token).ConfigureAwait(false);
            }
            else
            {
                await _tracker.PublishAsync(stamp(new ShardState(Name, request.Floor) { Action = ShardAction.Started }));
                await _commandBlock.PostAsync(Command.Started(highWaterMark, request.Floor));

                await _rebuild.Task.TimeoutAfterAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to rebuild projection {Name}", ProjectionShardIdentity);
            throw;
        }
        finally
        {
            // jasperfx#681: drop out of Rebuild BEFORE the teardown, so the Stopped state disposal
            // publishes does not still claim to be rebuilding. A consumer that tracks "is this shard
            // rebuilding" off the last state it saw would otherwise latch on rebuilding forever, which
            // is a worse failure than the one this issue reported. Safe here: everything that reads
            // Mode to decide the rebuild's outcome (ReportCriticalFailureAsync, the RangeCompleted
            // branch of the command loop) has already run against the completed _rebuild task.
            Mode = ShardExecutionMode.Continuous;

            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public void MarkSkipped(long sequence)
    {
        Metrics.IncrementSkips();
    }

    public Task RecordDeadLetterEventAsync(DeadLetterEvent @event)
    {
        return _runtime.RecordDeadLetterEventAsync(@event);
    }

    public Task RecordDeadLetterEventAsync(IEvent @event, Exception ex)
    {
        var dlEvent = new DeadLetterEvent(@event, Name, new ApplyEventException(@event, ex));
        return _runtime.RecordDeadLetterEventAsync(dlEvent);
    }

    public DateTimeOffset? PausedTime { get; private set; }

    /// <summary>
    /// jasperfx#565: the classified reason this agent last paused or stopped, or null while it is healthy.
    /// Set in <see cref="ReportCriticalFailureAsync(Exception)"/> and cleared by a start or a replay, so a
    /// supervisor polling it never reads a stale reason against a running agent.
    /// </summary>
    public ShardFailure? Failure { get; private set; }

    public ISubscriptionMetrics Metrics { get; }

    public async ValueTask MarkSuccessAsync(long processedCeiling)
    {
        var now = _timeProvider.GetUtcNow();
        await _commandBlock.PostAsync(Command.Completed(processedCeiling));
        await _tracker.PublishAsync(stamp(new ShardState(Name, processedCeiling)
        {
            Action = ShardAction.Updated,
            LastAdvanced = now,
            LastHeartbeat = now,
            AgentStatus = "Running"
        }));
    }

    // jasperfx#525: a deferred-rebuild execution buffered a range without committing. Advance the buffered
    // ceiling on the command loop so the loader keeps pumping the next page while committed progression waits
    // for the next flush.
    public async ValueTask MarkRangeBufferedAsync(long ceiling)
    {
        await _commandBlock.PostAsync(Command.RangeBuffered(ceiling));
    }

    public void MarkHighWater(long sequence)
    {
        _commandBlock.Post(Command.HighWaterMarkUpdated(sequence));
    }

    public ShardExecutionMode Mode { get; private set; } = ShardExecutionMode.Continuous;


    public async Task Apply(Command command, CancellationToken _)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        switch (command.Type)
        {
            case CommandType.HighWater:
                // Ignore the high water mark if it's lower than
                // already encountered. Not sure how that could happen,
                // but still be ready for that.
                if (command.HighWaterMark <= HighWaterMark)
                {
                    return;
                }

                HighWaterMark = command.HighWaterMark;
                break;

            case CommandType.Start:
                if (command.LastCommitted > command.HighWaterMark)
                {
                    throw new InvalidOperationException(
                        $"The last committed number ({command.LastCommitted}) cannot be higher than the high water mark ({command.HighWaterMark})");
                }

                HighWaterMark = command.HighWaterMark;
                LastCommitted = LastEnqueued = command.LastCommitted;
                _bufferedCeiling = LastCommitted; // jasperfx#525

                // The Mode guard matters for jasperfx#480: a bounded rebuild that disabled the
                // optimized replay (ReplayAsync above) posts Start in Rebuild mode with a replay
                // executor available — kicking off the executor here would overshoot the custom
                // ceiling. Unreachable in Rebuild mode before #480 (ReplayAsync would have taken
                // the optimized branch), so this is behavior-preserving for every other path.
                //
                // jasperfx#598/#610: the suppression guard matters for the same reason, and this is the
                // path that now needs it. A freshly deployed gated version starts Continuous at
                // LastCommitted 0, which is exactly this branch's trigger — and a store-implemented
                // replay executor replays to its OWN detected high-water, not to the gate mark, so it
                // would run straight past the prior version's mark and then hand off to continuous
                // execution with nothing left to suppress. Before #598 this could not happen because
                // the warm-up had already pushed committed progression to the mark before Continuous
                // ever started.
                if (Mode != ShardExecutionMode.Rebuild && !_sideEffectsSuppressed && LastCommitted == 0 &&
                    HighWaterMark > 0 && _execution.TryBuildReplayExecutor(out var executor))
                {
                    _logger.LogInformation("Starting optimized rebuild for projection/subscription {ShardName}",
                        Name.Identity);
                    var replayRequest =
                        new SubscriptionExecutionRequest(0, ShardExecutionMode.CatchUp, ErrorOptions, _runtime);
                    if (Name.TenantId != null)
                    {
                        // marten#4717: a tenant-scoped composite replays only to its own tenant's
                        // high-water (this agent's seeded HighWaterMark), not the store-wide max.
                        replayRequest = replayRequest with { StartingHighWater = HighWaterMark };
                    }

                    // #4721: run the optimized rebuild OFF this command-loop consumer. The replay executor
                    // posts a RangeCompleted command per page via MarkSuccessAsync; if we awaited it inline
                    // here, those posts would pile up in the bounded command channel whose only reader is
                    // THIS handler — once the channel fills (10k) the post blocks, the reader is blocked
                    // awaiting it, and the shard silently wedges at a batch boundary. Running it on a
                    // separate task frees this reader to drain those posts. The _replaying flag suppresses
                    // continuous loading until the rebuild signals completion with Command.ReplayCompleted.
                    _replaying = true;
                    _replayTask = Task.Run(() => runOptimizedRebuildAsync(executor, replayRequest));
                    return;
                }


                break;

            case CommandType.ReplayCompleted:
                // #4721: the off-consumer optimized rebuild finished. The trailing RangeCompleted posts it
                // emitted have already advanced LastCommitted to the replay ceiling (FIFO, single reader),
                // so align LastEnqueued and fall through to resume normal continuous operation — picking up
                // any events that arrived past the replay ceiling while the rebuild was running.
                _replaying = false;
                LastEnqueued = LastCommitted;
                break;

            case CommandType.RangeCompleted:
                LastCommitted = command.LastCommitted;
                if (LastCommitted > _bufferedCeiling)
                {
                    _bufferedCeiling = LastCommitted; // jasperfx#525: keep the buffered marker >= committed
                }

                await _tracker.PublishAsync(stamp(new ShardState(Name, LastCommitted)));

                if (LastCommitted == HighWaterMark && Mode == ShardExecutionMode.Rebuild)
                {
                    // We're done, get out of here!
                    _rebuild?.TrySetResult();
                }

                break;

            case CommandType.RangeBuffered:
                // jasperfx#525: a deferred rebuild buffered up to this ceiling without committing. Advance the
                // back-pressure marker (LastCommitted stays put) and fall through to keep pumping the next page.
                if (command.LastCommitted > _bufferedCeiling)
                {
                    _bufferedCeiling = command.LastCommitted;
                }

                break;

            case CommandType.WarmupSlotGranted:
                // jasperfx#598/#610: the throttle handed this shard a warm-up slot. Nothing to reconcile —
                // the standing was already published by the waiter; fall through and start loading.
                break;
        }

        // jasperfx#598/#610: the warm-up window closes the moment COMMITTED progression reaches the prior
        // version's mark. Committed (not enqueued, not buffered) is the whole point: it is what the next
        // start re-reads, so a crash anywhere inside the window resumes suppressed rather than replaying
        // side effects the prior version already emitted. Checked before the early returns below so the
        // agent flips and keeps loading in the same pass instead of stalling at its clamped ceiling.
        if (_sideEffectsSuppressed && LastCommitted >= Interlocked.Read(ref _sideEffectGateMark))
        {
            await completeSideEffectGateAsync().ConfigureAwait(false);
        }

        // Mind the gap! Measured against the REAL high water even inside the warm-up window, so a
        // suppressed shard still reports how far behind the store it genuinely is.
        Metrics.UpdateGap(HighWaterMark, LastCommitted);

        // #4721: while the off-consumer optimized rebuild is running we keep draining bookkeeping
        // (above) but must NOT kick off continuous loading — otherwise the normal loader would race
        // the rebuild over the same events. Command.ReplayCompleted clears the flag and falls through.
        if (_replaying)
        {
            return;
        }

        // jasperfx#598/#610: while inside the gate's warm-up window, only load once the daemon's warm-up
        // throttle has granted this shard a slot. Refusal is not a stall of the AGENT — it is started,
        // assigned, heartbeating and observable throughout, which is the entire point of moving the
        // warm-up out of the start path — it just isn't one of the N shards replaying right now.
        if (_sideEffectsSuppressed && !tryEnterWarmup())
        {
            return;
        }

        // jasperfx#598/#610: clamped to the gate mark while suppressed, the real high water otherwise.
        var ceiling = loadingCeiling();

        // jasperfx#525: measure in-flight (loaded-but-unprocessed) events against the buffered ceiling, not
        // committed progression. During a deferred rebuild LastCommitted lags until the next flush, so using it
        // here would make in-flight grow without bound and wedge the loader; the buffered ceiling advances as
        // each page is processed, so this stays equal to LastEnqueued - LastCommitted in every non-deferred path.
        var inflight = LastEnqueued - Math.Max(LastCommitted, _bufferedCeiling);

        // Back pressure, slow down
        if (inflight >= Options.MaximumHopperSize)
        {
            return;
        }

        // If all caught up, do nothing!
        // Not sure how either of these numbers could actually be higher than
        // the high water mark
        if (LastCommitted >= ceiling)
        {
            return;
        }

        if (LastEnqueued >= ceiling)
        {
            return;
        }

        // You could maybe get a full size batch, so go get the next
        if (ceiling - LastEnqueued > Options.BatchSize)
        {
            await loadNextAsync().ConfigureAwait(false);
        }
        else
        {
            // If the execution is busy, let's let events accumulate a little
            // more
            var twoBatchSize = 2 * Options.BatchSize;
            if (inflight < twoBatchSize)
            {
                await loadNextAsync().ConfigureAwait(false);
            }
        }
    }

    // #4721: drives a continuous-startup optimized rebuild on a task SEPARATE from the command-loop
    // consumer (see the CommandType.Start branch). Because this runs off the consumer, the replay
    // executor's per-page MarkSuccessAsync posts are free to be drained by Apply, so the bounded
    // command channel never deadlocks. On success it posts Command.ReplayCompleted so the agent
    // resumes continuous operation on the command-loop thread; on failure it routes through the normal
    // critical-failure path (which pauses/stops the shard and cancels), leaving _replaying moot.
    private async Task runOptimizedRebuildAsync(IReplayExecutor executor, SubscriptionExecutionRequest replayRequest)
    {
        try
        {
            await executor.StartAsync(replayRequest, this, _cancellation.Token).ConfigureAwait(false);

            // The replay executor swallows member failures and pauses/stops the agent rather than
            // throwing. Only resume continuous operation if the shard is still healthy.
            if (Status == AgentStatus.Running && !_cancellation.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Finished with optimized rebuild for projection/subscription {ShardName}, proceeding to normal, continuous operation",
                    Name.Identity);

                await _commandBlock.PostAsync(Command.ReplayCompleted()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Shutting down -- nothing to do.
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error trying to perform an optimized rebuild/replay of subscription {ShardName}",
                Name.Identity);
            await ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
    }

    private void startHeartbeatTimer()
    {
        _heartbeatTimer = new Timer(_ =>
        {
            if (_cancellation.IsCancellationRequested) return;

            try
            {
                var state = stamp(new ShardState(Name, LastCommitted)
                {
                    Action = ShardAction.Updated,
                    LastHeartbeat = _timeProvider.GetUtcNow(),
                    AgentStatus = Status.ToString()
                });

                _tracker.PublishAsync(state).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error publishing heartbeat for {Name}", ProjectionShardIdentity);
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private async Task loadNextAsync()
    {
        var request = new EventRequest
        {
            // jasperfx#598/#610: the clamped ceiling, so a page inside the side-effect gate's warm-up
            // window stops exactly at the prior version's mark rather than straddling it
            HighWater = loadingCeiling(),
            BatchSize = Options.BatchSize,
            Floor = LastEnqueued,
            ErrorOptions = ErrorOptions,
            Runtime = _runtime,
            Name = Name,
            Metrics = Metrics
        };

        try
        {
            var page = await _loader.LoadAsync(request, _cancellation.Token).ConfigureAwait(false);

            // Passing this along helps the individual executions "know" when to switch from
            // continuous mode to "catch up" and vice versa
            page.HighWaterMark = HighWaterMark;

            if (_logger.IsEnabled(LogLevel.Debug) && Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogDebug("Loaded {Number} of Events from {Floor} to {Ceiling} for Subscription {Name}",
                    page.Count, page.Floor, page.Ceiling, ProjectionShardIdentity);
            }

            LastEnqueued = page.Ceiling;

            await _execution.EnqueueAsync(page, this);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to load events");
            await ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
    }
}