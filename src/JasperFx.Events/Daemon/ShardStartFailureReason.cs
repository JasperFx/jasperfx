namespace JasperFx.Events.Daemon;

/// <summary>
///     Why a projection shard failed to start, as a value rather than as prose. Carried on
///     <see cref="ShardStartException.Reason" />.
/// </summary>
/// <remarks>
///     <para>
///     The four <see cref="JasperFxAsyncDaemon{TOperations,TQuerySession,TProjection}" /> start-failure cases
///     were already distinguished, but only in the exception's message — so the one thing a caller has to
///     decide (retry, or don't) could only be recovered by matching English text, which is not a contract.
///     See <see href="https://github.com/JasperFx/jasperfx/issues/912">#912</see>.
///     </para>
///     <para>
///     The responses genuinely differ. <see cref="HighWaterNotRunning" /> and <see cref="StartRace" /> are
///     races that a retry resolves; <see cref="ShardNotRegistered" /> is a configuration error that no
///     number of retries will fix, and retrying it is the permanent retry loop of wolverine#3519;
///     <see cref="AgentPaused" /> wants the pause reason surfaced rather than a restart.
///     <see cref="ShardStartException.IsTransient" /> encodes that split so every consumer does not have to
///     re-derive it.
///     </para>
/// </remarks>
public enum ShardStartFailureReason
{
    /// <summary>
    ///     Not classified. The default so that a <c>default(ShardStartFailureReason)</c> does not claim to be
    ///     one of the real cases, and so an exception built by a caller that has no classification to offer is
    ///     honest about it. Treated as non-transient — nothing auto-retries a failure it could not identify.
    /// </summary>
    Unknown,

    /// <summary>
    ///     An agent IS registered for this shard, in a status other than running. Almost always a shard that
    ///     an error paused. The useful response is to surface the pause reason; a restart only helps once the
    ///     underlying cause is resolved.
    /// </summary>
    AgentPaused,

    /// <summary>
    ///     High-water detection is not running yet, so the shard could not be positioned. A transient startup
    ///     race, most often on multi-store or Wolverine-managed hosts. Retry.
    /// </summary>
    HighWaterNotRunning,

    /// <summary>
    ///     No shard is registered with this store under the requested identity. A configuration error — the
    ///     name is wrong, or the projection was never registered. Never retry this one; report it as
    ///     configuration.
    /// </summary>
    ShardNotRegistered,

    /// <summary>
    ///     The shard is registered but did not start and reported no error, which points at a race between
    ///     concurrent agent starts on the same daemon. Retry.
    /// </summary>
    StartRace,

    /// <summary>
    ///     A start attempt faulted outright, and the cause is this exception's
    ///     <see cref="Exception.InnerException" />. Whether a retry helps depends on that inner exception, so
    ///     this is deliberately NOT reported as transient.
    /// </summary>
    Faulted
}
