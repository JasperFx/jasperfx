namespace JasperFx.Events.Daemon;

public enum ShardAction
{
    /// <summary>
    ///     The projection shard updated successfully
    /// </summary>
    Updated,

    /// <summary>
    ///     The projection shard was successfully started
    /// </summary>
    Started,

    /// <summary>
    ///     The projection shard was stopped
    /// </summary>
    Stopped,

    /// <summary>
    ///     The projection shard was paused and will be restarted
    ///     after a set amount of time based on error handling policies
    /// </summary>
    Paused,
    
    /// <summary>
    /// Recorded for the high water mark when it had to "skip" over stale
    /// data and potential holes in the event sequence
    /// </summary>
    Skipped,

    /// <summary>
    /// jasperfx#539: recorded for the high water mark when its poll loop was observed to have faulted
    /// (the watchdog saw an unhandled exception on the loop) before it is restarted.
    /// </summary>
    Faulted,

    /// <summary>
    /// jasperfx#539: recorded for the high water mark when the watchdog restarted the poll loop after it
    /// faulted or went stale (stopped completing cycles within the configured staleness threshold). The
    /// restart never advances the mark — it only re-establishes the loop.
    /// </summary>
    Restarted
}
