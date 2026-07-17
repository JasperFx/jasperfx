namespace JasperFx.Events.Daemon;

public record SubscriptionExecutionRequest(
    long Floor,
    ShardExecutionMode Mode,
    ErrorHandlingOptions ErrorHandling,
    IDaemonRuntime Runtime)
{
    /// <summary>
    /// marten#4717: the high-water ceiling the agent should start from. Null (default) uses the
    /// store-global ShardStateTracker mark — today's behavior. A tenant-scoped continuous agent passes
    /// its OWN tenant's high-water so it does not over-run to the store-global (max-tenant) mark; the
    /// agent's high-water can only be raised afterward (see SubscriptionAgent), so seeding it correctly
    /// at start is essential.
    /// </summary>
    public long? StartingHighWater { get; init; }

    /// <summary>
    /// jasperfx#480: force the plain event-loader replay path even when the store's execution can
    /// build an optimized IReplayExecutor. The blue/green side-effect gate replays to a CUSTOM
    /// ceiling (the prior version's mark) that store-implemented replay executors are not guaranteed
    /// to honor — they typically replay to their own detected high-water. Default false keeps
    /// today's behavior everywhere else.
    /// </summary>
    public bool DisableOptimizedReplay { get; init; }
}