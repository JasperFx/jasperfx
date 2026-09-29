namespace JasperFx.Events.Daemon;

/// <summary>
///     A projection shard failed to start
/// </summary>
/// <remarks>
///     <para>
///     jasperfx#912 — <see cref="Reason" /> classifies the failure, so a caller can decide whether to retry
///     without matching the message text. The message is unchanged; it stays the human-readable half.
///     </para>
///     <para>
///     The constructors are public so a consumer can construct one to test its own handling against. Nothing
///     but the daemon produces these in practice.
///     </para>
/// </remarks>
public class ShardStartException: Exception
{
    /// <summary>
    ///     A start attempt faulted outright. <see cref="Reason" /> is
    ///     <see cref="ShardStartFailureReason.Faulted" /> and the cause is <paramref name="innerException" />.
    /// </summary>
    public ShardStartException(string projectionIdentity, Exception innerException): base(
        $"Failure while trying to start '{projectionIdentity}'", innerException)
    {
        Reason = ShardStartFailureReason.Faulted;
    }

    /// <summary>
    ///     A start that did not fault but left no running agent. <paramref name="description" /> is appended to
    ///     the message as before.
    /// </summary>
    /// <param name="reason">
    ///     Classification of the failure. Defaults to <see cref="ShardStartFailureReason.Unknown" /> rather
    ///     than guessing, since an unclassified failure must not be reported as retryable.
    /// </param>
    public ShardStartException(string projectionIdentity, string description,
        ShardStartFailureReason reason = ShardStartFailureReason.Unknown): base(
        $"Unable to start a subscription agent for '{projectionIdentity}'. {description}")
    {
        Reason = reason;
    }

    /// <summary>
    ///     Why the start failed, as a value. See <see cref="ShardStartFailureReason" />.
    /// </summary>
    public ShardStartFailureReason Reason { get; }

    /// <summary>
    ///     True when the failure is a race that a retry is expected to resolve. False for a configuration
    ///     error, for a shard an error already paused, for an outright fault (where the inner exception decides),
    ///     and for anything unclassified.
    /// </summary>
    public bool IsTransient =>
        Reason is ShardStartFailureReason.HighWaterNotRunning or ShardStartFailureReason.StartRace;
}
