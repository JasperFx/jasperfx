using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public class SubscriptionAgent: ISubscriptionAgent, IAsyncDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly IEventLoader _loader;
    private readonly ISubscriptionExecution _execution;
    private readonly ShardStateTracker _tracker;
    private readonly ILogger _logger;
    public ShardName Name { get; }
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ActionBlock<Command> _commandBlock;
    private IDaemonRuntime _runtime = new NulloDaemonRuntime();

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

        _commandBlock = new ActionBlock<Command>(Apply, _cancellation.Token.SequentialOptions());

        ProjectionShardIdentity = name.Identity;
    }

    public AsyncOptions Options { get; }

    public string ProjectionShardIdentity { get; private set; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public ErrorHandlingOptions ErrorOptions { get; private set; } = new();

    // Making the setter internal so the test harness can override it
    // It's naughty, will make some people get very upset, and
    // makes unit testing much simpler. I'm not ashamed
    public long LastEnqueued { get; set; }

    public long LastCommitted { get; set; }

    public long HighWaterMark { get; set; }

    public async Task ReportCriticalFailureAsync(Exception ex, long lastProcessed)
    {
        await ReportCriticalFailureAsync(ex).ConfigureAwait(false);
        MarkSuccess(lastProcessed);
    }

    public async Task ReportCriticalFailureAsync(Exception ex)
    {
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            await _execution.HardStopAsync().ConfigureAwait(false);

            if (ex is ProgressionProgressOutOfOrderException)
            {
                PausedTime = null;
                Status = AgentStatus.Stopped;
                _tracker.Publish(new ShardState(Name, LastCommitted) { Action = ShardAction.Stopped, Exception = ex});
            }
            else
            {
                PausedTime = _timeProvider.GetUtcNow();
                Status = AgentStatus.Paused;
                _tracker.Publish(new ShardState(Name, LastCommitted) { Action = ShardAction.Paused, Exception = ex});
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
            await _commandBlock.Completion.ConfigureAwait(false);
            
            await _cancellation.CancelAsync().ConfigureAwait(false);

            await _execution.StopAndDrainAsync(token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Just get out of here.
            return;
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
            _logger.LogInformation("Stopped projection agent {Name}", ProjectionShardIdentity);
            Status = AgentStatus.Stopped;
        }
    }

    public async Task HardStopAsync()
    {
        await _execution.HardStopAsync().ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
        _tracker.Publish(new ShardState(Name, LastCommitted){Action = ShardAction.Stopped});
    }

    public Task StartAsync(SubscriptionExecutionRequest request)
    {
        Mode = request.Mode;
        _execution.Mode = request.Mode;
        ErrorOptions = request.ErrorHandling;
        _runtime = request.Runtime;

        _commandBlock.Post(Command.Started(_tracker.HighWaterMark, request.Floor));
        _tracker.Publish(new ShardState(Name, request.Floor){Action = ShardAction.Started});

        _logger.LogInformation("Started projection agent {Name}", ProjectionShardIdentity);
        return Task.CompletedTask;
    }

    private TaskCompletionSource? _rebuild;

    public async Task ReplayAsync(SubscriptionExecutionRequest request, long highWaterMark, TimeSpan timeout)
    {
        Mode = ShardExecutionMode.Rebuild;
        _rebuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _execution.Mode = ShardExecutionMode.Rebuild;
        ErrorOptions = request.ErrorHandling;
        _runtime = request.Runtime;
        LastCommitted = request.Floor; // Force it to start here!

        try
        {
            if (_execution.TryBuildReplayExecutor(out var executor))
            {
                _logger.LogInformation("Starting optimized rebuild for projection/subscription {ShardName}", Name.Identity);
                var cancellationSource = new CancellationTokenSource(timeout);
                await executor.StartAsync(request, this, cancellationSource.Token).ConfigureAwait(false);
            }
            else
            {
                _tracker.Publish(new ShardState(Name, request.Floor) { Action = ShardAction.Started });
                _commandBlock.Post(Command.Started(highWaterMark, request.Floor));

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

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _commandBlock.Complete();
        await _execution.DisposeAsync().ConfigureAwait(false);
    }


    public async Task Apply(Command command)
    {
        if (_cancellation.IsCancellationRequested) return;

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

                if (LastCommitted == 0 && HighWaterMark > 0 && _execution.TryBuildReplayExecutor(out var executor))
                {
                    try
                    {
                        _logger.LogInformation("Starting optimized rebuild for projection/subscription {ShardName}", Name.Identity);
                        await executor.StartAsync(new SubscriptionExecutionRequest(0, ShardExecutionMode.CatchUp, ErrorOptions, _runtime), this, _cancellation.Token).ConfigureAwait(false);
                        _logger.LogInformation("Finished with optimized rebuild for projection/subscription {ShardName}, proceeding to normal, continuous operation", Name.Identity);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error trying to perform an optimized rebuild/replay of subscription {ShardName}", Name.Identity);
                    }
                }


                break;

            case CommandType.RangeCompleted:
                LastCommitted = command.LastCommitted;
                _tracker.Publish(new ShardState(Name, LastCommitted));

                if (LastCommitted == HighWaterMark && Mode == ShardExecutionMode.Rebuild)
                {
                    // We're done, get out of here!
                    _rebuild?.TrySetResult();
                }

                break;
        }

        // Mind the gap!
        Metrics.UpdateGap(HighWaterMark, LastCommitted);

        var inflight = LastEnqueued - LastCommitted;

        // Back pressure, slow down
        if (inflight >= Options.MaximumHopperSize) return;

        // If all caught up, do nothing!
        // Not sure how either of these numbers could actually be higher than
        // the high water mark
        if (LastCommitted >= HighWaterMark) return;
        if (LastEnqueued >= HighWaterMark) return;

        // You could maybe get a full size batch, so go get the next
        if (HighWaterMark - LastEnqueued > Options.BatchSize)
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

    public ISubscriptionMetrics Metrics { get; }

    private async Task loadNextAsync()
    {
        var request = new EventRequest
        {
            HighWater = HighWaterMark,
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
                _logger.LogDebug("Loaded {Number} of Events from {Floor} to {Ceiling} for Subscription {Name}", page.Count, page.Floor, page.Ceiling, ProjectionShardIdentity);
            }

            LastEnqueued = page.Ceiling;

            _execution.Enqueue(page, this);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to load events");
            await ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
    }


    public void MarkSuccess(long processedCeiling)
    {
        _commandBlock.Post(Command.Completed(processedCeiling));
        _tracker.Publish(new ShardState(Name, processedCeiling){Action = ShardAction.Updated});
    }

    public void MarkHighWater(long sequence)
    {
        _commandBlock.Post(Command.HighWaterMarkUpdated(sequence));
    }

    public ShardExecutionMode Mode { get; private set; } = ShardExecutionMode.Continuous;



}
