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
    /// Replace the polled set wholesale — convenient when the daemon receives a fresh assignment snapshot
    /// from Wolverine rather than incremental activate/deactivate deltas.
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
