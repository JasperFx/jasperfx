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
    Skipped
}
