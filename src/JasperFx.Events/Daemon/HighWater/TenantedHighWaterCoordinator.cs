using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
/// Base-daemon mechanism that drives per-tenant high-water for a partitioned event store. It owns a
/// single <see cref="VectorizedHighWaterMonitor" /> per database (one vectorized agent, NOT one per
/// tenant), keeps the polled-tenant set in step with the shards currently assigned to this node, and
/// routes each tenant's high-water mark to that tenant's subscription agents only — so a stale/flat
/// tenant never stalls or skews another. The store supplies the real vectorized poll through
/// <see cref="IHighWaterDetector" />; this coordinator is pure, store-agnostic, and unit-testable in
/// isolation. Lives in the base daemon so Marten + Polecat inherit the behavior. jasperfx#407 Phase 2b.
/// </summary>
public class TenantedHighWaterCoordinator
{
    private readonly VectorizedHighWaterMonitor _monitor;
    private readonly IHighWaterDetector _detector;
    private readonly ILogger _logger;

    // jasperfx#539: UtcTicks of the last completed vectorized poll (0 == none yet). This is the liveness
    // heartbeat for the per-tenant high-water path — proof the coordinator is cycling — read from the
    // daemon watchdog thread, so written/read via Interlocked.
    private long _lastPolledAtTicks;

    // jasperfx#644: cycle supersession. Each PollAndRouteAsync stamps a new epoch; an in-flight cycle
    // checks the epoch per reading and retires itself as soon as a newer cycle (coalesced rerun, watchdog
    // restart, or an awaited priming poll) has started — the newer cycle re-reads every tenant, so nothing
    // is lost. Without this, the daemon watchdog's "abandon the hung poll and start fresh" left the
    // abandoned cycle RUNNING, stacking one full live cycle per staleness window until OOM.
    private int _pollEpoch;

    // jasperfx#644: single-flight + one trailing rerun for the daemon's background poll triggers.
    // _pollInFlight is 0 when free, otherwise the owning call's ticket — ownership matters so that a
    // cycle the watchdog abandoned cannot, on finally waking, release a guard that has since been handed
    // to a fresh cycle.
    private long _pollInFlight;
    private long _pollTicketSource;
    private int _pollQueued;

    // jasperfx#644: the last mark actually routed to agents / successfully persisted, per tenant. Lets the
    // steady-state cycle skip tenants whose mark did not advance instead of re-routing (one queued command
    // per agent per cycle) and re-persisting (one database write per tenant per cycle) 2,000+ unchanged
    // tenants forever. A failed persist is NOT recorded, so it retries next cycle exactly like the old
    // unconditional write did.
    private readonly Dictionary<string, long> _lastRoutedMarks = new();
    private readonly Dictionary<string, long> _lastPersistedMarks = new();
    private readonly object _marksLock = new();

    public TenantedHighWaterCoordinator(IHighWaterDetector detector, ILogger? logger = null)
    {
        _monitor = new VectorizedHighWaterMonitor(detector, logger);
        _detector = detector;
        _logger = logger ?? NullLogger.Instance;
    }

    public PolledTenantSet PolledTenants => _monitor.PolledTenants;

    /// <summary>
    /// jasperfx#539: heartbeat of the last completed per-tenant poll cycle — proof the coordinator is
    /// cycling, independent of whether any tenant's mark advanced. Null until the first poll.
    /// </summary>
    public DateTimeOffset? LastPolledAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPolledAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// jasperfx#539: true when the per-tenant poll has not completed a cycle within <paramref name="threshold"/>.
    /// Measured against heartbeat age (the poll cycling), NOT against any mark advancing.
    /// </summary>
    public bool IsStale(TimeSpan threshold, DateTimeOffset now)
    {
        var ticks = Interlocked.Read(ref _lastPolledAtTicks);
        if (ticks == 0)
        {
            return false;
        }

        return now - new DateTimeOffset(ticks, TimeSpan.Zero) > threshold;
    }

    /// <summary>
    /// The rebuild ceiling for a tenant — its latest observed high-water mark. Null until first polled.
    /// </summary>
    public long? CeilingFor(string tenantId) => _monitor.CeilingFor(tenantId);

    /// <summary>
    /// Reconcile the polled-tenant set with the shards currently assigned to this node: a tenant is
    /// polled exactly when at least one of its shards is running here. Store-global shards (null
    /// <see cref="ShardName.TenantId" />) contribute nothing — they stay on the global high-water agent.
    /// </summary>
    public void SyncAssignedTenants(IEnumerable<ShardName> assignedShards)
    {
        var tenants = assignedShards
            .Select(x => x.TenantId)
            .Where(tenantId => tenantId != null)
            .Distinct()
            .ToList();

        _monitor.PolledTenants.SetTenants(tenants!);
    }

    /// <summary>
    /// Poll the per-tenant high-water vector once and push each tenant's mark to that tenant's agents.
    /// Tenants are detected and routed independently; an empty polled set is a no-op. Returns the readings
    /// for observability/testing.
    /// </summary>
    public async Task<IReadOnlyList<TenantHighWaterReading>> PollAndRouteAsync(
        IReadOnlyList<ISubscriptionAgent> agents, CancellationToken token)
    {
        var epoch = Interlocked.Increment(ref _pollEpoch);

        var readings = await _monitor.PollAsync(token).ConfigureAwait(false);

        // jasperfx#539: a completed vectorized poll is one cycle of the per-tenant high-water path — stamp
        // the liveness heartbeat even when no tenant's mark moved.
        Interlocked.Exchange(ref _lastPolledAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        // jasperfx#644: group the agents by tenant once — O(agents) — instead of scanning every agent for
        // every reading, which is O(tenants × agents) per cycle and at field scale (2,173 tenants ×
        // ~4,400 agents) was ~9.5 million name comparisons per poll, on a timer, forever.
        Dictionary<string, List<ISubscriptionAgent>>? agentsByTenant = null;
        foreach (var agent in agents)
        {
            var agentTenant = agent.Name.TenantId;
            if (agentTenant == null)
            {
                continue;
            }

            agentsByTenant ??= new Dictionary<string, List<ISubscriptionAgent>>();
            if (!agentsByTenant.TryGetValue(agentTenant, out var bucket))
            {
                agentsByTenant[agentTenant] = bucket = new List<ISubscriptionAgent>();
            }

            bucket.Add(agent);
        }

        foreach (var reading in readings)
        {
            // jasperfx#644: retire this cycle as soon as a newer one has started. The newer cycle re-reads
            // every tenant with fresher statistics and the shared last-routed/last-persisted bookkeeping
            // keeps the hand-off gapless, so two full cycles never grind to completion side by side.
            if (token.IsCancellationRequested || Volatile.Read(ref _pollEpoch) != epoch)
            {
                break;
            }

            var mark = reading.Statistics.CurrentMark;

            // Route only to that tenant's shards. A tenant's stale mark never reaches another tenant.
            // jasperfx#644: and only when the mark actually advanced — agents drop a non-advancing mark
            // anyway (SubscriptionAgent.Apply ignores <= HighWaterMark) and a newly started tenant agent
            // is seeded from CeilingFor at start, so re-routing an unchanged mark every cycle was pure
            // per-cycle allocation (one queued command per agent, forever).
            if (reading.TenantId != null && agentsByTenant != null &&
                agentsByTenant.TryGetValue(reading.TenantId, out var tenantAgents))
            {
                bool advanced;
                lock (_marksLock)
                {
                    advanced = !_lastRoutedMarks.TryGetValue(reading.TenantId, out var lastRouted) ||
                               mark > lastRouted;
                }

                if (advanced)
                {
                    foreach (var agent in tenantAgents)
                    {
                        agent.MarkHighWater(mark);
                    }

                    lock (_marksLock)
                    {
                        _lastRoutedMarks[reading.TenantId] = mark;
                    }
                }
            }

            // marten#4717: persist a durable per-tenant high-water row so each tenant's mark survives
            // a daemon restart (the store-global HighWaterMark row cannot represent multiple tenants).
            // jasperfx#449: carry the per-tenant timestamp through so the persisted row exposes
            // per-tenant staleness. No-op for detectors that don't override the write.
            // jasperfx#644: skipped when the mark has not advanced past the last successfully persisted
            // value — the row already says exactly this, and re-writing it for thousands of flat tenants
            // per cycle was the bulk of each cycle's database work.
            if (reading.TenantId != null && mark > 0)
            {
                bool shouldPersist;
                lock (_marksLock)
                {
                    shouldPersist = !_lastPersistedMarks.TryGetValue(reading.TenantId, out var lastPersisted) ||
                                    mark > lastPersisted;
                }

                if (shouldPersist)
                {
                    try
                    {
                        await _detector
                            .MarkHighWaterForTenantAsync(reading.TenantId, mark,
                                reading.Statistics.Timestamp, token)
                            .ConfigureAwait(false);

                        lock (_marksLock)
                        {
                            _lastPersistedMarks[reading.TenantId] = mark;
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error persisting per-tenant high-water mark for tenant {TenantId}",
                            reading.TenantId);
                    }
                }
            }
        }

        return readings;
    }

    /// <summary>
    /// jasperfx#644: single-flight wrapper over <see cref="PollAndRouteAsync" /> for the daemon's
    /// background triggers (the OnNext high-water fast path and the cadence timer). At most one cycle runs
    /// at a time; a trigger that arrives mid-cycle latches exactly ONE trailing rerun instead of stacking
    /// another concurrent full cycle — at thousands of tenants a cycle is slow enough that unguarded
    /// triggers pile live cycles up without bound (the gh-644 OOM). Awaited priming paths that need a
    /// completed poll for a just-activated tenant keep calling <see cref="PollAndRouteAsync" /> directly.
    /// Returns true when this call ran at least one cycle, false when it coalesced into an in-flight one.
    /// </summary>
    public async Task<bool> PollAndRouteCoalescedAsync(
        Func<IReadOnlyList<ISubscriptionAgent>> agentsSource, CancellationToken token)
    {
        while (true)
        {
            var ticket = Interlocked.Increment(ref _pollTicketSource);
            if (Interlocked.CompareExchange(ref _pollInFlight, ticket, 0) != 0)
            {
                // A cycle is running; its owner picks this up as one trailing rerun.
                Interlocked.Exchange(ref _pollQueued, 1);
                return false;
            }

            try
            {
                do
                {
                    await PollAndRouteAsync(agentsSource(), token).ConfigureAwait(false);
                } while (!token.IsCancellationRequested &&
                         Interlocked.CompareExchange(ref _pollQueued, 0, 1) == 1);
            }
            finally
            {
                // Release only if still the owner — AbandonInFlightPoll may have already handed the
                // guard to a fresh cycle while this one was wedged.
                Interlocked.CompareExchange(ref _pollInFlight, 0, ticket);
            }

            // Close the release race: a trigger that latched between the loop's last check and the
            // release above would otherwise be dropped. If another caller has already re-acquired the
            // flag, the top of the loop re-latches and hands the rerun to them.
            if (token.IsCancellationRequested || Interlocked.CompareExchange(ref _pollQueued, 0, 1) != 1)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// jasperfx#644: the daemon watchdog's restart seam. Releases the single-flight guard so a fresh cycle
    /// can start even though a wedged one never returned, and bumps the epoch so the wedged cycle — which
    /// is still running, "abandoned" only in the sense that nobody awaits it — retires itself at its next
    /// reading instead of grinding on as a leaked concurrent full cycle.
    /// </summary>
    public void AbandonInFlightPoll()
    {
        Interlocked.Increment(ref _pollEpoch);
        Volatile.Write(ref _pollQueued, 0);
        Interlocked.Exchange(ref _pollInFlight, 0L);
    }
}
