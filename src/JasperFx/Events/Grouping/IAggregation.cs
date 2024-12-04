using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

public interface IAggregation : IProjectionBatch
{
    Task ProcessAggregationAsync<TDoc, TId>(EventSliceGroup<TDoc,TId> group, CancellationToken cancellation);
}