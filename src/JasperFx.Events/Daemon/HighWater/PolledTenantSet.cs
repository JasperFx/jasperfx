namespace JasperFx.Events.Daemon.HighWater;

/// <summary>
/// The dynamic set of tenants a node's vectorized high-water agent is currently polling. The daemon
/// updates this as Wolverine (re)distributes projection shards across nodes, so high-water polling cost
/// is proportional to the work actually assigned to this node rather than to the total tenant count.
/// A tenant is activated when one of its shards lands on this node and deactivated when its last shard
/// leaves. Thread-safe so assignment changes and polling can race freely. jasperfx#407 Phase 2.
/// </summary>
public sealed class PolledTenantSet
{
    private readonly object _lock = new();

    // Each polled tenant, with a stamp of its membership: a new one every time the tenant joins the set,
    // kept for as long as it stays. A tenant that leaves and comes back may be a new incarnation of itself
    // (a tenant removed and re-added at runtime starts its event sequence again at 1), and the stamp is what
    // lets VectorizedHighWaterMonitor tell the two apart.
    private readonly Dictionary<string, long> _tenants = new();
    private long _lastMembership;

    // Tenants held in the set for the duration of an in-flight agent start, reference counted because
    // several shards of the same tenant can be starting at once. See Pin.
    private readonly Dictionary<string, int> _pins = new();

    /// <summary>
    /// Add a tenant to the polled set. Returns true if it was newly added.
    /// </summary>
    public bool Activate(string tenantId)
    {
        lock (_lock)
        {
            return join(tenantId);
        }
    }

    // Callers hold _lock.
    private bool join(string tenantId)
    {
        if (_tenants.ContainsKey(tenantId)) return false;

        _tenants[tenantId] = ++_lastMembership;
        return true;
    }

    /// <summary>
    /// Remove a tenant from the polled set. Returns true if it was present and removed.
    /// </summary>
    /// <remarks>
    /// jasperfx#710: a pinned tenant is never removed, and this returns false for it. Without that,
    /// the obvious-looking call here would silently defeat <see cref="Pin" /> and reintroduce the
    /// field failure jasperfx#702 fixed — an agent whose start is still in flight losing its tenant
    /// from the very poll it is waiting on.
    /// </remarks>
    public bool Deactivate(string tenantId)
    {
        lock (_lock)
        {
            if (_pins.ContainsKey(tenantId)) return false;

            return _tenants.Remove(tenantId);
        }
    }

    /// <summary>
    /// Hold a tenant in the polled set until <see cref="Unpin" />, so a wholesale
    /// <see cref="SetTenants" /> cannot drop it. The daemon pins a tenant for the duration of an agent
    /// start: the assignment snapshot a concurrent start or stop reconciles against is built from the
    /// agents already REGISTERED on the node, which by definition does not include one whose start is
    /// still in flight. Dropped mid-start, that tenant is missing from the very poll the start is
    /// waiting on, and the agent is then seeded with no ceiling at all. Reference counted, because
    /// several shards of one tenant can be starting at the same time.
    /// </summary>
    public void Pin(string tenantId)
    {
        lock (_lock)
        {
            _pins[tenantId] = _pins.TryGetValue(tenantId, out var count) ? count + 1 : 1;
            join(tenantId);
        }
    }

    /// <summary>
    /// Release a <see cref="Pin" />. The tenant stays in the polled set — whether it belongs there is
    /// decided by the next <see cref="SetTenants" />, which is exactly the reconciliation the daemon
    /// runs when a start succeeds or fails.
    /// </summary>
    public void Unpin(string tenantId)
    {
        lock (_lock)
        {
            if (!_pins.TryGetValue(tenantId, out var count)) return;

            if (count <= 1)
            {
                _pins.Remove(tenantId);
            }
            else
            {
                _pins[tenantId] = count - 1;
            }
        }
    }

    /// <summary>
    /// Replace the polled set wholesale — convenient when the daemon receives a fresh assignment snapshot
    /// from Wolverine rather than incremental activate/deactivate deltas. Pinned tenants survive. A tenant
    /// in both the old and the new set never left it, so <see cref="VectorizedHighWaterMonitor" /> keeps
    /// holding its mark monotonically.
    /// </summary>
    public void SetTenants(IEnumerable<string> tenantIds)
    {
        lock (_lock)
        {
            var assigned = new HashSet<string>(tenantIds);
            assigned.UnionWith(_pins.Keys);

            var departed = _tenants.Keys.Where(x => !assigned.Contains(x)).ToList();
            foreach (var tenantId in departed)
            {
                _tenants.Remove(tenantId);
            }

            foreach (var tenantId in assigned)
            {
                join(tenantId);
            }
        }
    }

    public bool IsPolled(string tenantId)
    {
        lock (_lock)
        {
            return _tenants.ContainsKey(tenantId);
        }
    }

    /// <summary>
    /// A point-in-time copy of the currently polled tenants, safe to enumerate while assignments change.
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _tenants.Keys.ToList();
        }
    }

    // A point-in-time copy of the polled tenants with the stamp of each one's membership.
    internal Dictionary<string, long> SnapshotMemberships()
    {
        lock (_lock)
        {
            return new Dictionary<string, long>(_tenants);
        }
    }

    // The stamp of the tenant's current membership, or null when it is not polled. A tenant that leaves the
    // set and joins it again gets a new one.
    internal long? MembershipOf(string tenantId)
    {
        lock (_lock)
        {
            return _tenants.TryGetValue(tenantId, out var membership) ? membership : null;
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _tenants.Count;
            }
        }
    }
}
