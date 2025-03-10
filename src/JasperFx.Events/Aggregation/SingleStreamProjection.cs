using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;

namespace JasperFx.Events.Aggregation;

public abstract class SingleStreamProjection<TDoc, TId, TOperations, TQuerySession> : AggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IAggregatorSource<TQuerySession>, IAggregator<TDoc, TQuerySession>, IInlineProjection<TOperations> 
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Func<IEvent,TId> _identitySource;
    private readonly Func<StreamAction, TId> _streamActionSource;

    protected SingleStreamProjection(Type[] transientExceptionTypes) : base(AggregationScope.SingleStream, transientExceptionTypes)
    {
        _identitySource = IEvent.CreateAggregateIdentitySource<TId>();
        _streamActionSource = StreamAction.CreateAggregateIdentitySource<TId>();
    }

    protected sealed override IEventSlicer buildSlicer(TQuerySession session)
    {
        // Doesn't hurt anything if it's not actually tenanted
        return new TenantedEventSlicer<TDoc, TId>(new ByStream<TDoc, TId>());
    }

    Type IAggregatorSource<TQuerySession>.AggregateType => typeof(TDoc);

    IAggregator<T, TQuerySession> IAggregatorSource<TQuerySession>.Build<T>()
    {
        return this.As<IAggregator<T, TQuerySession>>();
    }

    public async ValueTask<TDoc> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot, CancellationToken cancellation)
    {
        if (!events.Any()) return snapshot;
        
        // get the id off of the event
        var action = await ApplyAsync(session, snapshot, _identitySource(events[0]), events, cancellation);
        
        // TODO -- what the heck to do here if it's null?
        
        return action.Snapshot;
    }

    public override IInlineProjection<TOperations> BuildForInline()
    {
        return this;
    }

    async Task IInlineProjection<TOperations>.ApplyAsync(TOperations session, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        if (streams.Count == 0) return;
        
        if (streams.Count == 1)
        {
            var stream = streams[0];
            var storage = session.ProjectionStorageFor<TDoc, TId>(stream.TenantId);
            var id = _streamActionSource(stream);
            var snapshot = await storage.LoadAsync(id, cancellation);
            var action = await ApplyAsync(session, snapshot, id, stream.Events, cancellation);
            storage.ApplyInline(action, id, stream.TenantId);
        }
        
        var groups = streams.GroupBy(x => x.TenantId).ToArray();
        foreach (var group in groups)
        {
            var storage = session.ProjectionStorageFor<TDoc, TId>(group.Key);
            var ids = group.Select(x => _streamActionSource(x)).ToArray();
            
            var snapshots = await storage.LoadManyAsync(ids, cancellation);
            foreach (var stream in group)
            {
                var id = _streamActionSource(stream);
                snapshots.TryGetValue(id, out var snapshot);
                var action = await ApplyAsync(session, snapshot, id, stream.Events, cancellation);
                storage.ApplyInline(action, id, stream.TenantId);
            }
        }
    }
}