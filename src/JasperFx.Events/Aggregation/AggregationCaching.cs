using ImTools;
using JasperFx.Core;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

// TODO -- this is ONLY spiked in. Not sure about the usage yet. 
public class AggregationCaching
{
    private readonly ISubscriptionExecution[] _executions;
    private ImHashMap<Type, object> _maps = ImHashMap<Type, object>.Empty;

    public AggregationCaching(IEnumerable<ISubscriptionExecution> executions)
    {
        _executions = executions.ToArray();
    }

    public IAggregateCache<TId, TDoc> FindCache<TId, TDoc>(string tenantId)
    {
        if (_maps.TryFind(typeof(TDoc), out var value))
        {
            if (value is IAggregateCaching<TId, TDoc> caching) return caching.CacheFor(tenantId);
        }

        foreach (var execution in _executions)
        {
            if (execution.TryGetAggregateCache<TId, TDoc>(out var cache))
            {
                _maps = _maps.AddOrUpdate(typeof(TDoc), cache);
                return cache.CacheFor(tenantId);
            }
        }

        return new NulloAggregateCache<TId, TDoc>();
    }
}

public interface IAggregateCaching<TId, TDoc>
{
    IAggregateCache<TId, TDoc> CacheFor(string tenantId);
}

