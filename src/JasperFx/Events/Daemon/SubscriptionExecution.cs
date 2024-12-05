using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace JasperFx.Events.Daemon;

public interface ISubscriptionRunner : IAsyncDisposable
{
    string ShardIdentity { get; }
    Task ExecuteAsync(EventRange range, ShardExecutionMode mode, CancellationToken token);

    string DatabaseIdentifier { get; }
}

public class SubscriptionExecution: ISubscriptionExecution
{
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ActionBlock<EventRange> _executionBlock;
    private readonly ISubscriptionRunner _runner;

    public SubscriptionExecution(ISubscriptionRunner runner, ILogger logger)
    {
        _logger = logger;

        _runner = runner;

        _executionBlock = new ActionBlock<EventRange>(executeRange, _cancellation.Token.SequentialOptions());
    }

    private async Task executeRange(EventRange range)
    {
        if (_cancellation.IsCancellationRequested) return;

        using var activity = range.Agent.Metrics.TrackExecution(range);

        try
        {
            await _runner.ExecuteAsync(range, Mode, _cancellation.Token).ConfigureAwait(false);

            range.Agent.MarkSuccess(range.SequenceCeiling);

            if (Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogInformation("Subscription '{ShardIdentity}': Executed for {Range}",
                    ShardIdentity, range);
            }

            range.Agent.Metrics.UpdateProcessed(range.Size);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            activity?.RecordException(e);
            _logger.LogError(e, "Error trying to process subscription {Name}", ShardIdentity);
            await range.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
        finally
        {
            activity?.Stop();
        }
    }

    public string ShardIdentity => _runner.ShardIdentity;

    public async ValueTask DisposeAsync()
    {
        await _runner.DisposeAsync().ConfigureAwait(false);
    }

    public void Enqueue(EventPage page, ISubscriptionAgent subscriptionAgent)
    {
        if (_cancellation.IsCancellationRequested) return;

        var range = new EventRange(subscriptionAgent, page.Floor, page.Ceiling)
        {
            Events = page
        };

        _executionBlock.Post(range);
    }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        _executionBlock.Complete();
        await _executionBlock.Completion.ConfigureAwait(false);
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync().ConfigureAwait(false);
#else
        _cancellation.Cancel();
#endif
    }

    public async Task HardStopAsync()
    {
        _executionBlock.Complete();
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync().ConfigureAwait(false);
#else
        _cancellation.Cancel();
#endif
    }

    public Task EnsureStorageExists()
    {
        return Task.CompletedTask;
    }

    public string DatabaseName => _runner.DatabaseIdentifier;
    public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;
    public bool TryBuildReplayExecutor(out IReplayExecutor executor)
    {
        executor = default;
        return false;
    }
}
