namespace JasperFx.Events.Projections;

public record SubscriptionExecutionRequest(
    long Floor,
    ShardExecutionMode Mode,
    ErrorHandlingOptions ErrorHandling,
    IDaemonRuntime Runtime);