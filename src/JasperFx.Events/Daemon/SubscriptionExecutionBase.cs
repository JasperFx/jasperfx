using System.Diagnostics.CodeAnalysis;
using JasperFx.Blocks;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

/// <summary>
///     Implement on IEventStorage!
/// </summary>
/// <typeparam name="T"></typeparam>
public interface ISubscriptionRunner<T>
{
    Task ExecuteAsync(T subscription, IEventDatabase database, EventRange range, ShardExecutionMode mode,
        CancellationToken token);
}

public class SubscriptionExecution<T> : SubscriptionExecutionBase
{
    private readonly ISubscriptionRunner<T>? _runner;
    private readonly T _subscription;

    public SubscriptionExecution(object storage, T subscription, IEventDatabase database, ShardName name,
        ILogger logger) : base(database, name, logger)
    {
        _runner = storage as ISubscriptionRunner<T>;
        if (_runner == null)
        {
            throw new ArgumentOutOfRangeException(nameof(storage),
                $"Must implement {typeof(ISubscriptionRunner<T>).FullNameInCode()}");
        }

        _subscription = subscription;
    }

    protected override Task executeRangeAsync(IEventDatabase database, EventRange range, ShardExecutionMode mode,
        CancellationToken cancellationToken)
    {
        return _runner!.ExecuteAsync(_subscription, database, range, Mode, cancellationToken);
    }
}

public abstract class SubscriptionExecutionBase : ISubscriptionExecution
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IEventDatabase _database;
    private readonly Block<EventRange> _executionBlock;
    private readonly ILogger _logger;


    public SubscriptionExecutionBase(IEventDatabase database, ShardName name, ILogger logger)
    {
        _database = database;
        _logger = logger;

        _executionBlock = new Block<EventRange>(executeRange);

        // TODO -- revisit this. 
        ShardIdentity = $"{name.Identity}@{database.Identifier}";
    }

    public string ShardIdentity { get; }

    public ValueTask DisposeAsync()
    {
        _executionBlock.Complete();
        return new ValueTask();
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

        return _executionBlock.PostAsync(range);
    }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        await _executionBlock.WaitForCompletionAsync().ConfigureAwait(false);
        await _cancellation.CancelAsync().ConfigureAwait(false);
    }

    public async Task HardStopAsync()
    {
        _executionBlock.Complete();
        await _cancellation.CancelAsync().ConfigureAwait(false);
    }

    public ShardExecutionMode Mode { get; set; } = ShardExecutionMode.Continuous;

    public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    private async Task executeRange(EventRange range, CancellationToken _)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        using var activity = range.Agent.Metrics.TrackExecution(range);

        try
        {
            await executeRangeAsync(_database, range, Mode, _cancellation.Token);

            await range.Agent.MarkSuccessAsync(range.SequenceCeiling);

            if (Mode == ShardExecutionMode.Continuous)
            {
                _logger.LogInformation("Subscription '{ShardIdentity}': Executed for {Range}",
                    ShardIdentity, range);
            }

            range.Agent.Metrics.UpdateProcessed(range.Size);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            activity?.AddException(e);
            _logger.LogError(e, "Error trying to process subscription {Name}", ShardIdentity);
            await range.Agent.ReportCriticalFailureAsync(e).ConfigureAwait(false);
        }
        finally
        {
            activity?.Stop();
        }
    }

    protected abstract Task executeRangeAsync(IEventDatabase database, EventRange range, ShardExecutionMode mode,
        CancellationToken cancellationToken);
}