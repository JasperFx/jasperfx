using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// A request to rebuild one identity of a single-stream projection inside the shard that runs it
/// (jasperfx#1201). The rebuild is bound to the next batch the shard builds: that batch re-folds the
/// identity from nothing over every event of its stream up to the batch's ceiling, takes the identity out
/// of its own page, and commits the result together with the shard's progression.
/// </summary>
/// <param name="TenantId">The tenant the identity belongs to. Use <see cref="StorageConstants.DefaultTenantId" /> when the store is not multi-tenanted.</param>
/// <param name="Identity">The identity of the projected document, which for a single-stream projection is its stream's identity.</param>
public sealed record IdentityRebuildRequest(string TenantId, object Identity)
{
    /// <summary>
    /// Runs inside the bound batch, after the re-fold succeeded and before the batch commits, so a caller's
    /// own write commits with the rebuild or not at all. Cast the batch to
    /// <see cref="IProjectionBatch{TOperations,TQuerySession}" /> to reach a session.
    /// </summary>
    public Func<IProjectionBatch, ValueTask>? InSameBatch { get; init; }

    /// <summary>
    /// Runs after the bound batch commits and before the shard builds its next batch.
    /// </summary>
    public Action? OnCommitted { get; init; }

    /// <summary>
    /// marten#6003: discard semantics. An event that still fails in the re-fold is dropped and dead-lettered, the
    /// way a skipped event is, and the fold carries on without it. Off by default, so a failing event faults the
    /// request and leaves the identity as it was.
    /// </summary>
    public bool SkipFailingEvents { get; init; }
}

/// <summary>
/// What a per-identity rebuild did.
/// </summary>
/// <param name="BoundCeiling">The ceiling of the batch the rebuild was bound to. The identity's document is consistent with every event of its stream up to and including this sequence.</param>
/// <param name="Action">What the re-fold wrote: <see cref="ActionType.Store" />, a delete, or <see cref="ActionType.Nothing" /> when the stream folds to nothing and no document is left.</param>
public sealed record IdentityRebuildResult(long BoundCeiling, ActionType Action)
{
    /// <summary>
    /// marten#6003: how many events the re-fold dropped and dead-lettered under
    /// <see cref="IdentityRebuildRequest.SkipFailingEvents" />.
    /// </summary>
    public int SkippedEvents { get; init; }

    /// <summary>
    /// marten#6003: the identity was parked, and this rebuild unparked it.
    /// </summary>
    public bool Unparked { get; init; }
}

/// <summary>
/// Why a per-identity rebuild was refused.
/// </summary>
public enum IdentityRebuildRefusal
{
    /// <summary>The projection is not a single-stream aggregation, or is a member of a composite.</summary>
    NotASingleStreamProjection,

    /// <summary>The event store does not implement the re-fold loader.</summary>
    NotSupportedByStore,

    /// <summary>The shard is being rebuilt or replayed. A full rebuild already re-folds every identity.</summary>
    ShardIsRebuilding,

    /// <summary>The shard has never committed a batch, so there is nothing to repair yet.</summary>
    ShardHasNeverCommitted,

    /// <summary>No daemon on this node is running the shard.</summary>
    ShardNotRunningHere,

    /// <summary>The shard's progression shows it running on another node, so an offline rebuild here would race it.</summary>
    ShardLiveOnAnotherNode,

    /// <summary>The shard stopped or paused before the rebuild could be bound to a batch.</summary>
    ShardStopped
}

/// <summary>
/// A per-identity rebuild was refused before anything was written (jasperfx#1201).
/// </summary>
public class IdentityRebuildRefusedException : Exception
{
    public IdentityRebuildRefusedException(string projectionName, object identity, IdentityRebuildRefusal reason,
        string? detail = null)
        : base(messageFor(projectionName, identity, reason, detail))
    {
        ProjectionName = projectionName;
        Identity = identity;
        Reason = reason;
    }

    public string ProjectionName { get; }

    public object Identity { get; }

    public IdentityRebuildRefusal Reason { get; }

    private static string messageFor(string projectionName, object identity, IdentityRebuildRefusal reason,
        string? detail)
    {
        var why = reason switch
        {
            IdentityRebuildRefusal.NotASingleStreamProjection =>
                "only a single-stream projection running on its own shard can rebuild one identity",
            IdentityRebuildRefusal.NotSupportedByStore => "the event store does not support per-identity rebuilds",
            IdentityRebuildRefusal.ShardIsRebuilding => "the projection's shard is being rebuilt or replayed",
            IdentityRebuildRefusal.ShardHasNeverCommitted => "the projection's shard has not committed anything yet",
            IdentityRebuildRefusal.ShardNotRunningHere => "no daemon on this node is running the projection's shard",
            IdentityRebuildRefusal.ShardLiveOnAnotherNode =>
                "the projection's shard is running on another node; rebuild it there",
            IdentityRebuildRefusal.ShardStopped => "the projection's shard stopped before the rebuild was bound to a batch",
            _ => reason.ToString()
        };

        var message = $"Cannot rebuild '{identity}' of projection '{projectionName}': {why}.";
        return detail == null ? message : message + " " + detail;
    }
}

/// <summary>
/// What a store's re-fold loader reads for a per-identity rebuild: one stream's events in
/// (<see cref="AfterSequence" />, <see cref="Ceiling" />], in sequence order, at most <see cref="Limit" /> of
/// them (jasperfx#1201).
/// </summary>
/// <remarks>
/// Apply the same rules as the daemon's own event loader for the projection, so a re-fold writes what the
/// shard would have written: only <see cref="EventTypes" /> when it is not empty, the tenant's events only,
/// archived events only when <see cref="IncludeArchivedEvents" /> is set.
/// </remarks>
public sealed class IdentityRebuildEventRequest
{
    public IdentityRebuildEventRequest(string tenantId, object streamIdentity, long afterSequence, long ceiling,
        int limit)
    {
        TenantId = tenantId;
        StreamIdentity = streamIdentity;
        AfterSequence = afterSequence;
        Ceiling = ceiling;
        Limit = limit;
    }

    public string TenantId { get; }

    /// <summary>
    /// The projected identity as the projection knows it: a <see cref="Guid" />, a <see cref="string" />, or a
    /// strong-typed identifier wrapping one, which the store unwraps.
    /// </summary>
    public object StreamIdentity { get; }

    /// <summary>Exclusive lower bound on the event sequence.</summary>
    public long AfterSequence { get; }

    /// <summary>Inclusive upper bound on the event sequence.</summary>
    public long Ceiling { get; }

    /// <summary>The most events to return. Fewer means there are no more.</summary>
    public int Limit { get; }

    /// <summary>The event types the projection handles. Empty means every type.</summary>
    public IReadOnlyList<Type> EventTypes { get; init; } = [];

    /// <summary>Whether the projection reads archived events.</summary>
    public bool IncludeArchivedEvents { get; init; }

    /// <summary>
    /// marten#6003: skip (and dead-letter) an event the store can't deserialize instead of throwing. Off by default,
    /// and deliberately not taken from the store's continuous error options: a replay of an identity parked on an
    /// unreadable event has to fail on it again, not skip the event it is meant to repair. A discard sets it.
    /// </summary>
    public bool SkipSerializationErrors { get; init; }
}

/// <summary>
/// Helpers for a store's own <c>RebuildSingleStreamAsync</c> to decide whether it may rebuild an identity
/// outside the daemon (jasperfx#1201).
/// </summary>
public static class IdentityRebuildGuard
{
    /// <summary>
    /// How recently a progression row has to have been written by a running agent to count as live. Matches
    /// the subscription agent's heartbeat cadence with room for a missed beat.
    /// </summary>
    public static readonly TimeSpan DefaultLiveWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Does the shard's progression row show an agent running it right now? True when the row carries a
    /// running node or a running agent status, and was written within <paramref name="liveWindow" />.
    /// </summary>
    /// <remarks>
    /// The node and status columns are only written when extended progression is on, so without them a quiet
    /// shard can look stopped. That is the safe direction for a store that routes through its own daemon
    /// first: the only case this has to catch is a shard another node is advancing, and advancing rewrites the
    /// row.
    /// </remarks>
    public static bool IsLive(ShardState progression, DateTimeOffset now, TimeSpan liveWindow)
    {
        if (progression.AgentStatus is { } status &&
            !string.Equals(status, nameof(AgentStatus.Running), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lastWritten = progression.LastHeartbeat ?? progression.LastUpdated ?? progression.LastAdvanced;
        return lastWritten.HasValue && now - lastWritten.Value <= liveWindow;
    }

    /// <summary>
    /// Refuse a rebuild outside the daemon with <see cref="IdentityRebuildRefusal.ShardLiveOnAnotherNode" />
    /// when <paramref name="progression" /> shows the shard live.
    /// </summary>
    public static void AssertNotLiveElsewhere(string projectionName, object identity, ShardState? progression,
        DateTimeOffset now, TimeSpan? liveWindow = null)
    {
        if (progression != null && IsLive(progression, now, liveWindow ?? DefaultLiveWindow))
        {
            var node = progression.RunningOnNode.HasValue ? $" (node {progression.RunningOnNode})" : "";
            throw new IdentityRebuildRefusedException(projectionName, identity,
                IdentityRebuildRefusal.ShardLiveOnAnotherNode,
                $"Shard '{progression.ShardName}' was last written at {progression.LastUpdated ?? progression.LastHeartbeat}{node}.");
        }
    }

    internal static TId CoerceIdentity<TId>(string projectionName, object identity)
    {
        if (identity is TId id) return id;

        throw new ArgumentOutOfRangeException(nameof(identity),
            $"Projection '{projectionName}' is keyed by {typeof(TId).FullNameInCode()}, but the identity to rebuild was a {identity.GetType().FullNameInCode()}.");
    }
}
