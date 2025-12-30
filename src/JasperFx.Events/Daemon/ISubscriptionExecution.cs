using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public interface ISubscriptionExecution: IAsyncDisposable
{
    ShardName ShardName { get; }
    
    ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent);

    Task StopAndDrainAsync(CancellationToken token);
    Task HardStopAsync();

    ShardExecutionMode Mode { get; set; }
    bool TryBuildReplayExecutor([NotNullWhen(true)]out IReplayExecutor? executor);
    Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events, CancellationToken cancellation);
    
    Task ProcessRangeAsync(EventRange range);

    /// <summary>
    /// Try to find an aggregate cache for the designated id and aggregate type
    /// </summary>
    /// <param name="caching"></param>
    /// <typeparam name="TId"></typeparam>
    /// <typeparam name="TDoc"></typeparam>
    /// <returns></returns>
    bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching);
}

/// <summary>
/// Use to create an optimized projection or subscription replay in the case of rewinding all the way
/// back to sequence = 0 (projection rebuilds most likely)
/// </summary>
public interface IReplayExecutor
{
    Task StartAsync(SubscriptionExecutionRequest request,
        ISubscriptionController controller, CancellationToken cancellation);
}
