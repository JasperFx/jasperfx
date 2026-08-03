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
    /// Force the plain event-loader replay path even when the store's execution can build an
    /// optimized IReplayExecutor. Store-implemented replay executors are not guaranteed to honor a
    /// CUSTOM ceiling — they typically replay to their own detected high-water — so any caller that
    /// needs <see cref="ISubscriptionAgent.ReplayAsync"/> to stop at a bounded mark must set this.
    /// Default false keeps today's behavior everywhere else.
    /// </summary>
    public bool DisableOptimizedReplay { get; init; }

    /// <summary>
    /// jasperfx#598/#610: the blue/green side-effect gate mark (jasperfx#480) — the highest PRIOR
    /// version's persisted progression. When this is above <see cref="Floor"/> on a Continuous start,
    /// the agent starts immediately but suppresses side effects, clamps its loading ceiling to this
    /// mark so no page ever straddles it, and enables side effects the moment its committed
    /// progression reaches it. Zero (the default) means no gate.
    ///
    /// <para>
    /// This replaces the pre-#598 shape, where the warm-up ran as a bounded replay INSIDE the start
    /// path: a shard start that normally costs milliseconds cost tens of seconds to minutes, and an
    /// agent did not count as assigned until its replay finished.
    /// </para>
    /// </summary>
    public long SideEffectGateMark { get; init; }
}