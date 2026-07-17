namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
///     A standalone, display-only high-water detector: just the high-water agent for a single
///     <see cref="IEventDatabase" />, running on one node, with no projection shards attached. It exists so a
///     monitoring tool (CritterWatch) can show a live event-store "ceiling" for stores whose projections are all
///     Inline/Live and therefore run no async daemon — there is otherwise no high-water progression to display.
///     See <see href="https://github.com/JasperFx/CritterWatch/issues/675" />.
///     <para>
///     This is deliberately narrower than <see cref="IProjectionDaemon" />: it neither starts nor tracks any
///     projection, so standing one up does not require registering a projection. The caller owns the lifecycle —
///     start it on exactly one node (reuse the host's leader/agent election rather than a bespoke one) and stop it
///     when the node stands down. Observe progression via <see cref="Tracker" />; the running loop publishes a
///     <see cref="Projections.ShardState.HighWaterMark" /> state whenever the mark advances, exactly as a full
///     daemon's high-water agent does.
///     </para>
/// </summary>
public interface IHighWaterMonitor : IAsyncDisposable
{
    /// <summary>
    ///     Subject URI of the <see cref="IEventDatabase" /> this monitor polls, matching
    ///     <see cref="IHighWaterDetector.DatabaseUri" />.
    /// </summary>
    Uri DatabaseUri { get; }

    /// <summary>
    ///     Observable the monitor publishes high-water progression to. A consumer subscribes here for the same
    ///     <see cref="Projections.ShardState.HighWaterMark" /> states a full daemon would emit.
    /// </summary>
    ShardStateTracker Tracker { get; }

    /// <summary>
    ///     The most recently observed high-water mark, or 0 before the first successful detection.
    /// </summary>
    long CurrentMark { get; }

    /// <summary>
    ///     Is the recurring polling loop currently running?
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Start the recurring high-water polling loop. Call on exactly one node.
    /// </summary>
    Task StartAsync(CancellationToken token);

    /// <summary>
    ///     Stop the recurring polling loop. The monitor may be restarted with <see cref="StartAsync" />.
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     Perform a single on-demand high-water detection without relying on the recurring loop — useful for a
    ///     one-shot "where is the ceiling right now?" read.
    /// </summary>
    Task<HighWaterStatistics> DetectAsync(CancellationToken token);
}
