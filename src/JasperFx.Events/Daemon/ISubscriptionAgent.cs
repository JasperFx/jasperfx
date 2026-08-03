namespace JasperFx.Events.Daemon;

/// <summary>
///     Used internally by asynchronous projections.
/// </summary>
// This is public because it's used by the generated code
public interface ISubscriptionAgent : ISubscriptionController
{
    long Position { get; }
    AgentStatus Status { get; }

    /// <summary>
    /// jasperfx#525: the effective high-water ceiling this agent is driving toward. During a rebuild this is
    /// the target the replay stops at (the store-wide high water, or a bounded ceiling such as the jasperfx#480
    /// prior-version mark), so a deferred-flush execution can recognize the final range and force a flush. The
    /// default returns 0 for agents that don't track it; the daemon's own agent overrides with the real value.
    /// </summary>
    long HighWaterMark => 0;

    DateTimeOffset? PausedTime { get; }

    /// <summary>
    /// jasperfx#565: WHY this agent was paused or stopped, if it was. <see cref="Status"/> alone told an
    /// external supervisor (Wolverine's <c>EventSubscriptionAgent</c>, which wraps a shard as a
    /// distributed agent) that a shard had paused but never what to do about it, so progress could
    /// flatline with no actionable alert. Set alongside <see cref="Status"/> when a failure is reported,
    /// and cleared when the agent starts or replays.
    ///
    /// <para>
    /// Defaulted to null so implementations that don't track failures — test doubles, wrappers that
    /// delegate — are unaffected. A wrapper around a live inner agent should delegate this the same way
    /// it delegates <see cref="Status"/>.
    /// </para>
    /// </summary>
    ShardFailure? Failure => null;

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

    /// <summary>
    /// jasperfx#598/#610: true while this agent is inside the blue/green side-effect gate's warm-up
    /// window — running normally in Continuous mode, but over events the PRIOR version of the
    /// projection already processed, so <c>RaiseSideEffects</c> must not fire for them. The agent
    /// clears this the moment its committed progression reaches the prior version's mark.
    ///
    /// <para>
    /// Read by the executions at range-execution time (they only see the agent through
    /// <see cref="EventRange.Agent"/>), which is why it lives here rather than on the execution.
    /// Defaulted to false so wrappers and test doubles are unaffected; a wrapper around a live inner
    /// agent should delegate this the same way it delegates <see cref="Status"/>.
    /// </para>
    /// </summary>
    bool SideEffectsSuppressed => false;
}