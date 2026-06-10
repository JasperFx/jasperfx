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
}