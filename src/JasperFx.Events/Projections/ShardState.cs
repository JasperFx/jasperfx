using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

/// <summary>
/// Whether the shard that published a <see cref="ShardState" /> was running normally or rebuilding.
/// </summary>
/// <remarks>
/// jasperfx#681: <see cref="rebuilding" /> had no writer anywhere in the tree until then, so every
/// state a <c>SubscriptionAgent</c> published reported <see cref="continuous" /> — including the ones
/// published during a replay. An observer therefore could not tell a projection catching up under a
/// rebuild from one running normally, which is the distinction this enum exists to draw.
/// </remarks>
public enum ShardMode
{
    /// <summary>
    /// Unknown. Not published by a running agent — it is the default for a persisted progression row
    /// that predates the mode being recorded, so it means "no mode was stored", not "idle".
    /// </summary>
    none,

    /// <summary>
    /// Running normally. Covers ordinary catch-up as well as steady-state tailing: a shard behind the
    /// high water mark under continuous operation is still <see cref="continuous" />, because the
    /// number an operator is watching is lag either way.
    /// </summary>
    continuous,

    /// <summary>
    /// Rebuilding from the beginning of the stream. The sequence on the same state is catch-up
    /// progress toward the rebuild's ceiling, <em>not</em> lag — which is what makes this worth
    /// telling an operator about.
    /// </summary>
    rebuilding
}

/// <summary>
///     Point in time state of a single projection shard or the high water mark
/// </summary>
public class ShardState
{
    public const string HighWaterMark = "HighWaterMark";
    public const string AllProjections = "AllProjections";

    public ShardState(string shardName, long sequence)
    {
        ShardName = shardName;
        Sequence = sequence;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public ShardState(ShardName shardName, long sequence): this(shardName.Identity, sequence)
    {
        TenantId = shardName.TenantId;
    }

    /// <summary>
    /// Tenant partition this shard state belongs to. Null means store-global, which is the
    /// only behavior that existed before per-tenant partitioning. Serializes as null for
    /// existing consumers that never set it.
    /// </summary>
    public string? TenantId { get; set; }

    public long RebuildThreshold { get; set; }

    public ShardMode Mode { get; set; } = ShardMode.continuous;

    public int AssignedNodeNumber { get; set; } = 0;

    public ShardAction Action { get; set; } = ShardAction.Updated;

    /// <summary>
    ///     Time this state was recorded
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    ///     Name of the projection shard
    /// </summary>
    public string ShardName { get; }

    /// <summary>
    ///     Furthest event sequence number processed by this projection shard
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    ///     If not null, this is the exception that caused this state to be published
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Marks the previous "good" high water mark when the ShardState is publishing a "skipping"
    /// event
    /// </summary>
    public long PreviousGoodMark { get; set; }

    /// <summary>
    /// Last time the shard successfully advanced its position
    /// </summary>
    public DateTimeOffset? LastAdvanced { get; set; }

    /// <summary>
    /// Last heartbeat received from the shard's subscription agent.
    /// <para>
    /// On a state published to the live tracker this beats every 10 seconds while the agent runs
    /// (<c>SubscriptionAgent</c>'s heartbeat timer), so it is a usable liveness signal for an
    /// <c>IObserver&lt;ShardState&gt;</c> subscribed to a running daemon. On a state HYDRATED FROM
    /// THE PROGRESSION TABLE it is not: since jasperfx#622 the periodic beat is not persisted by
    /// default, so the stored value freezes at the last Started / Paused / Stopped transition. See
    /// <see cref="JasperFx.Events.ProjectionProgressRow.LastHeartbeat" />.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastHeartbeat { get; set; }

    /// <summary>
    /// Current status of the subscription agent (e.g. "Running", "Paused", "Stopped")
    /// </summary>
    public string? AgentStatus { get; set; }

    /// <summary>
    /// If paused, the reason for the pause (typically an exception message)
    /// </summary>
    public string? PauseReason { get; set; }

    /// <summary>
    /// jasperfx#565: the classified reason this shard was paused or stopped — category, the failing
    /// event when one can be blamed, and the exception text — as a plain serializable value an external
    /// supervisor can act on. <see cref="Exception"/> still carries the live exception for in-process
    /// observers; this is what survives a hop to Wolverine's assignment plane, a persisted extended
    /// progression row, or a CritterWatch alert. Null on every state that isn't reporting a failure.
    ///
    /// <para>
    /// <see cref="PauseReason" /> is kept in lockstep (it is <see cref="ShardFailure.Detail"/>), so
    /// consumers that only read the string are unaffected.
    /// </para>
    /// </summary>
    public ShardFailure? Failure { get; set; }

    /// <summary>
    /// The node number that is currently running this shard
    /// </summary>
    public int? RunningOnNode { get; set; }

    /// <summary>
    /// Configured threshold for warning-level health check when shard is behind by this many events.
    /// Null means use default.
    /// </summary>
    public long? WarningBehindThreshold { get; set; }

    /// <summary>
    /// Configured threshold for critical-level health check when shard is behind by this many events.
    /// Null means use default.
    /// </summary>
    public long? CriticalBehindThreshold { get; set; }

    /// <summary>
    /// Count of event sequences the HighWater agent has flagged as skipped.
    /// Implementations decide whether this is cumulative (running total over
    /// the lifetime of the daemon) or most-recent (events skipped in the
    /// current <see cref="ShardAction.Skipped"/> publication) — both reach
    /// the same operator conclusion: "this projection skipped some events".
    /// Null when the implementation hasn't populated it — CritterWatch
    /// renders null as "n/a" rather than 0 to distinguish "no skips
    /// reported" from "zero skips have happened".
    ///
    /// Surfaces CritterWatch#150 signal 3 ("High Water Agent skipped
    /// events"). Daemon-emitted; only meaningful on the HighWaterMark
    /// shard.
    /// </summary>
    public long? SkippedEventsCount { get; set; }

    /// <summary>
    /// Subject URI of the <see cref="IEventStore"/> this shard state belongs
    /// to (e.g. <c>marten://main</c>, <c>marten://itarievenstore</c>). Set
    /// by the publisher — either the daemon itself when it constructs the
    /// state or, more commonly, a per-store decorator observer that stamps
    /// the URI before forwarding to the registered observer chain (see
    /// Wolverine's <c>EventStoreAgents</c>). Null on legacy paths or when
    /// the publisher hasn't been updated yet — consumers that need to
    /// attribute the state to a specific store should fall back to a
    /// store-by-shard-name lookup (see CritterWatch's
    /// <c>ServiceSummaryProjection.ResolveStoreUri</c>).
    ///
    /// Surfaces JasperFx/ProductSupport#5: live shard states and
    /// HighWaterMark snapshots on multi-store hosts collapsed into a single
    /// bucket on the CritterWatch side because nothing in the daemon chain
    /// stamped the owning store. This field is the abstraction the per-store
    /// decorator stamps onto so downstream consumers don't need a side
    /// channel.
    /// </summary>
    public string? StoreUri { get; set; }

    /// <summary>
    /// <see cref="IEventDatabase.Identifier"/> of the database this shard state came from. An event store
    /// backed by more than one database — database-per-tenant, sharded tenancy — runs one daemon per
    /// database, and every one of them publishes the same shard names (<c>Trip:All</c>) and the same
    /// <c>HighWaterMark</c>. Without this, a consumer keying on shard name alone collapses all of them into
    /// one entry and reports whichever database published last.
    ///
    /// Stamped by <see cref="Daemon.ShardStateTracker.DatabaseIdentifier"/> when the daemon is constructed,
    /// so it rides both the subscription agents' states and the high-water agent's marks. Null only when
    /// the publisher predates this field.
    ///
    /// Surfaces JasperFx/CritterWatch#678: a database-per-tenant service reported a high-water mark of 0 and
    /// every projection as "awaiting its first event" while its per-tenant daemons were actively running.
    /// </summary>
    public string? DatabaseIdentifier { get; set; }

    /// <summary>
    /// jasperfx#598/#610: true while this shard is running inside the blue/green side-effect gate's
    /// warm-up window (see <c>AsyncOptions.GateSideEffectsBehindPriorVersion</c>) — it is genuinely
    /// running and advancing, but over events the PRIOR version of the projection already processed,
    /// so its side effects are deliberately suppressed until it reaches that version's mark.
    ///
    /// <para>
    /// Before #598 this distinction was invisible: the warm-up ran inside the start path, so the shard
    /// simply did not exist yet as far as any observer was concerned. Now that it starts immediately,
    /// an operator needs to be able to tell "running, but not yet emitting side effects" from "running
    /// normally" without reading pod logs.
    /// </para>
    /// </summary>
    public bool SideEffectsSuppressed { get; set; }

    /// <summary>
    /// jasperfx#598/#610: when <see cref="SideEffectsSuppressed"/> is true, the prior version's
    /// progression mark this shard is catching up to before its side effects are enabled. Together
    /// with <see cref="Sequence"/> this is the warm-up's progress bar. Null otherwise.
    /// </summary>
    public long? SideEffectGateMark { get; set; }

    public override string ToString()
    {
        return $"{nameof(ShardName)}: {ShardName}, {nameof(Sequence)}: {Sequence}, {nameof(Action)}: {Action}";
    }

    protected bool Equals(ShardState other)
    {
        return ShardName == other.ShardName && Sequence == other.Sequence;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((ShardState)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return ((ShardName != null ? ShardName.GetHashCode() : 0) * 397) ^ Sequence.GetHashCode();
        }
    }
}
