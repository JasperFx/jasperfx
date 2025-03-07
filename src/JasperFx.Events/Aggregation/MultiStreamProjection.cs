using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Aggregation;

public abstract class MultiStreamProjection<TDoc, TId, TOperations, TQuerySession> :
    AggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IInlineProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
{
    protected MultiStreamProjection(Type[] transientExceptionTypes) : base(AggregationScope.MultiStream, transientExceptionTypes)
    {
    }

    public override IInlineProjection<TOperations> BuildForInline()
    {
        return this;
    }

    protected override IEventSlicer buildSlicer()
    {
        throw new NotImplementedException();
    }

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToArray();
        var slicer = buildSlicer();
        
        var groups = await slicer.SliceAsync(events);
        foreach (var group in groups.OfType<SliceGroup<TDoc, TId>>())
        {
            var storage = operations.ProjectionStorageFor<TDoc, TId>(group.TenantId);
            var ids = group.Slices.Select(x => x.Id).ToArray();
            
            var snapshots = await storage.LoadManyAsync(ids, cancellation);
            foreach (var slice in group.Slices)
            {
                snapshots.TryGetValue(slice.Id, out var snapshot);
                var action = await ApplyAsync(operations, snapshot, slice.Id, slice.Events(), cancellation);
                storage.ApplyInline(action, slice.Id, group.TenantId);
            }
        }
    }
}