using JasperFx.Core;

namespace JasperFx.Events.Daemon;

/// <summary>
///     Turns the daemon state observed after a start that left no running agent into a
///     <see cref="ShardStartFailureReason" /> and the message that goes with it.
/// </summary>
/// <remarks>
///     <para>
///     A pure function over the three things the decision actually depends on, rather than a method on the
///     daemon, so the mapping from state to classification is testable without standing up a daemon in each
///     of four states. jasperfx#912 is about the classification being a contract; a contract that can only
///     be exercised through a full daemon start is not much of one.
///     </para>
///     <para>
///     The order of the checks is the order of specificity and is load-bearing: a registered-but-paused
///     agent explains the miss on its own, and asking whether the shard is registered at all is only
///     interesting once the two transient explanations are ruled out.
///     </para>
/// </remarks>
internal static class ShardStartFailureClassifier
{
    /// <param name="registeredStatus">
    ///     The status of an agent already registered under this identity, or null when none is.
    /// </param>
    /// <param name="highWaterIsRunning">Whether high-water detection is up.</param>
    /// <param name="knownShardIdentities">
    ///     The store's registered shard identities. A delegate rather than a value because this is only
    ///     needed on the third branch, and the daemon should not pay a registry read on the two paths that
    ///     answer before it.
    /// </param>
    /// <param name="identity">The shard identity whose start failed.</param>
    internal static (ShardStartFailureReason, string) Classify(
        AgentStatus? registeredStatus,
        bool highWaterIsRunning,
        Func<IReadOnlyList<string>> knownShardIdentities,
        string identity)
    {
        if (registeredStatus.HasValue)
        {
            return (ShardStartFailureReason.AgentPaused,
                $"An agent is registered for this shard in status '{registeredStatus.Value}' rather than running. It was most likely paused by an error; check the log for the pause reason and restart the shard once resolved.");
        }

        if (!highWaterIsRunning)
        {
            return (ShardStartFailureReason.HighWaterNotRunning,
                "High-water detection is not running yet, so the shard could not be positioned. This is typically a transient startup race; retrying the start once high-water detection is up should succeed.");
        }

        var known = knownShardIdentities();
        if (!known.Contains(identity, StringComparer.OrdinalIgnoreCase))
        {
            return (ShardStartFailureReason.ShardNotRegistered,
                $"No such shard is registered with this store. Known shards are: {known.Join(", ")}.");
        }

        return (ShardStartFailureReason.StartRace,
            "The shard is registered but did not start and did not report an error, which points at a startup race between concurrent agent starts on this daemon. Retrying the start usually succeeds.");
    }
}
