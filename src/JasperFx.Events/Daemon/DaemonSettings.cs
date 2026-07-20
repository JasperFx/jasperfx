using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Events.Daemon.HighWater;

namespace JasperFx.Events.Daemon;

public interface IReadOnlyDaemonSettings
{
    /// <summary>
    ///     If the projection daemon detects a "stale" event sequence that is probably cause
    ///     by sequence numbers being reserved, but never committed, this is the threshold to say
    ///     "just look for the highest contiguous sequence number newer than X amount of time" to trigger
    ///     the daemon to continue advancing. The default is 3 seconds.
    /// </summary>
    TimeSpan StaleSequenceThreshold { get; }

    /// <summary>
    ///     Polling time between looking for a new high water sequence mark
    ///     if the daemon detects low activity. The default is 1 second.
    /// </summary>
    TimeSpan SlowPollingTime { get; }

    /// <summary>
    ///     Polling time between looking for a new high water sequence mark
    ///     if the daemon detects high activity. The default is 250ms
    /// </summary>
    TimeSpan FastPollingTime { get; }

    /// <summary>
    ///     Polling time for the running projection daemon to determine the health
    ///     of its activities and try to restart anything that is not currently running
    /// </summary>
    TimeSpan HealthCheckPollingTime { get; }

    /// <summary>
    ///     How long the high-water agent's poll loop may go without completing a cycle (its liveness
    ///     heartbeat) before the health-check watchdog treats it as wedged and restarts it. This is
    ///     measured against heartbeat age — the loop cycling — NOT against the mark advancing, so a
    ///     quiet store with no new events is never considered stale. The default is 30 seconds.
    /// </summary>
    TimeSpan HighWaterStalenessThreshold { get; }

    /// <summary>
    ///     Projection Daemon mode. The default is Disabled
    /// </summary>
    DaemonMode AsyncMode { get; }
}

public class DaemonSettings: IReadOnlyDaemonSettings
{
    /// <summary>
    /// All daemon related metrics and activity spans will use this prefix
    /// </summary>
    public string OtelPrefix { get; set; }
    
    /// <summary>
    /// For Open Telemetry tracing. May be null to denote disabled Activity tracking within the Daemon
    /// </summary>
    public ActivitySource? ActivitySource { get; set; }
    
    public const int RebuildBatchSize = 1000;

    /// <summary>
    ///     This is used to establish a global lock id for the async daemon and should
    ///     be unique for any applications that target the same database.
    /// </summary>
    public int DaemonLockId { get; set; } = 4444;

    /// <summary>
    ///     Time in milliseconds to poll for leadership election in the async projection daemon
    /// </summary>
    public int LeadershipPollingTime { get; set; } = 5000;

    /// <summary>
    ///     If the projection daemon detects a "stale" event sequence that is probably cause
    ///     by sequence numbers being reserved, but never committed, this is the threshold to say
    ///     "just look for the highest contiguous sequence number newer than X amount of time" to trigger
    ///     the daemon to continue advancing. The default is 3 seconds.
    /// </summary>
    public TimeSpan StaleSequenceThreshold { get; set; } = 3.Seconds();

    /// <summary>
    ///     Polling time between looking for a new high water sequence mark
    ///     if the daemon detects low activity. The default is 1 second.
    /// </summary>
    public TimeSpan SlowPollingTime { get; set; } = 1.Seconds();

    /// <summary>
    ///     Polling time between looking for a new high water sequence mark
    ///     if the daemon detects high activity. The default is 250ms
    /// </summary>
    public TimeSpan FastPollingTime { get; set; } = 250.Milliseconds();

    /// <summary>
    ///     Polling time for the running projection daemon to determine the health
    ///     of its activities and try to restart anything that is not currently running
    /// </summary>
    public TimeSpan HealthCheckPollingTime { get; set; } = 5.Seconds();

    /// <summary>
    ///     jasperfx#539: how long the high-water agent's poll loop may go without completing a cycle (its
    ///     liveness heartbeat) before the health-check watchdog treats it as wedged and restarts it. Measured
    ///     against heartbeat age — the loop cycling — NOT against the mark advancing, so a quiet store with no
    ///     new events is never considered stale. The default of 30 seconds sits comfortably above
    ///     <see cref="SlowPollingTime"/> × several cycles plus <see cref="StaleSequenceThreshold"/>.
    /// </summary>
    public TimeSpan HighWaterStalenessThreshold { get; set; } = 30.Seconds();

    /// <summary>
    /// If a subscription has been paused for any reason
    /// </summary>
    public TimeSpan AgentPauseTime { get; set; } = 1.Seconds();

    /// <summary>
    ///     Projection Daemon mode. The default is Disabled.
    /// </summary>
    public DaemonMode AsyncMode { get; set; } = DaemonMode.Disabled;

    /// <summary>
    /// Optional mechanism to wake the high water detection agent when new events
    /// are appended, instead of relying solely on polling. For example, a PostgreSQL
    /// LISTEN/NOTIFY implementation can signal immediately when events are written.
    /// When null, the agent falls back to polling with <see cref="FastPollingTime"/>
    /// and <see cref="SlowPollingTime"/>.
    /// </summary>
    public IDaemonWakeup? Wakeup { get; set; }

    /// <summary>
    /// jasperfx#494 (epic #486 WS2): cap on how many subscription agents may execute
    /// IEventLoader.LoadAsync concurrently against one database. Every load opens its own
    /// store session, so without a bound the connection pool's high-water mark trends toward
    /// the number of running agents — under per-tenant event partitioning that is
    /// (projections × tenants) even though only a handful of loads are ever active at once.
    /// Applies per daemon instance (one daemon per store × database). Zero or negative
    /// disables the throttle.
    /// </summary>
    public int MaxConcurrentEventLoadsPerDatabase { get; set; } = 4;

    /// <summary>
    /// Epic #486 WS3: cap on how many projection batches may execute their SQL (the
    /// commit round-trip) concurrently against one database. Each agent's batch opens its
    /// own session to write documents + progression, so without a bound a burst of active
    /// agents — e.g. every (projection × tenant) agent committing its first batch right
    /// after daemon start — drives the connection pool's high-water mark toward the agent
    /// count. Measurement (jasperfx#494) attributed ~29 of ~35 steady-state daemon
    /// connections to these commit sessions. Applies per daemon instance (one daemon per
    /// store × database). Zero or negative disables the governor.
    /// </summary>
    public int MaxConcurrentBatchWritesPerDatabase { get; set; } = 4;

    /// <summary>
    /// jasperfx#420 (epic #486 WS3): cap on how many projection rebuild cells — the
    /// (projection × tenant/shard) cross product — may run concurrently within one database
    /// during a rebuild operation. Rebuild only; continuous catch-up is governed by
    /// <see cref="MaxConcurrentEventLoadsPerDatabase"/> and
    /// <see cref="MaxConcurrentBatchWritesPerDatabase"/> instead. Null means "derive a
    /// default store-side" — concrete stores fall back to a value derived from the
    /// connection pool size (Marten/Polecat use <c>max(1, poolSize / 8)</c>) via their
    /// <see cref="IEventStore.MaxConcurrentRebuildsPerDatabase"/> override; JasperFx.Events
    /// itself is store-agnostic and treats null as unbounded. Zero or negative disables the
    /// cap. The <c>projections rebuild --max-concurrent</c> CLI flag overrides per operation.
    /// </summary>
    public int? MaxConcurrentRebuildsPerDatabase { get; set; }
}
