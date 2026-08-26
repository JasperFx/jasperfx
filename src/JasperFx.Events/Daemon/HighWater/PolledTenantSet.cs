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
    private readonly HashSet<string> _tenants = new();

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
            return _tenants.Add(tenantId);
        }
    }

    /// <summary>
    /// Remove a tenant from the polled set. Returns true if it was present.
    /// </summary>
    public bool Deactivate(string tenantId)
    {
        lock (_lock)
        {
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
            _tenants.Add(tenantId);
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
    /// from Wolverine rather than incremental activate/deactivate deltas. Pinned tenants survive.
    /// </summary>
    public void SetTenants(IEnumerable<string> tenantIds)
    {
        lock (_lock)
        {
            _tenants.Clear();
            foreach (var tenantId in tenantIds)
            {
                _tenants.Add(tenantId);
            }

            foreach (var pinned in _pins.Keys)
            {
                _tenants.Add(pinned);
            }
        }
    }

    public bool IsPolled(string tenantId)
    {
        lock (_lock)
        {
            return _tenants.Contains(tenantId);
        }
    }

    /// <summary>
    /// A point-in-time copy of the currently polled tenants, safe to enumerate while assignments change.
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _tenants.ToList();
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
