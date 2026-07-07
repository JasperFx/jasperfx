namespace JasperFx.Events.Daemon;

/// <summary>
///     Used internally by asynchronous projections.
/// </summary>
// This is public because it's used by the generated code
public interface ISubscriptionAgent : ISubscriptionController
{
    long Position { get; }
    AgentStatus Status { get; }

    DateTimeOffset? PausedTime { get; }
    ISubscriptionMetrics Metrics { get; }
    void MarkHighWater(long sequence);

    Task StopAndDrainAsync(CancellationToken token);
    Task HardStopAsync();

    Task StartAsync(SubscriptionExecutionRequest request);

    /// <summary>
    ///     Record a dead letter event for the failure to process the current
    ///     event
    /// </summary>
    /// <param name="event"></param>
    /// <returns></returns>
    Task RecordDeadLetterEventAsync(DeadLetterEvent @event);

    Task ReplayAsync(SubscriptionExecutionRequest request, long highWaterMark, TimeSpan timeout);
    
    /// <summary>
    /// Mark an event as having been skipped during asynchronous messaging. This helps
    /// track execution metrics
    /// </summary>
    /// <param name="sequence"></param>
    void MarkSkipped(long sequence);

    /// <summary>
    /// Epic #486 WS3: the daemon-owned governor bounding concurrent projection batch
    /// execute/commit sessions against this agent's database. Null = unbounded. Surfaced
    /// here so the projection executions (which only see the agent via EventRange) can
    /// share the daemon-wide bound.
    /// </summary>
    SemaphoreSlim? BatchWriteThrottle => null;
}