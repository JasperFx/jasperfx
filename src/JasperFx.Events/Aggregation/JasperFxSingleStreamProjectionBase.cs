using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public abstract class JasperFxSingleStreamProjectionBase<TDoc, TId, TOperations, TQuerySession> : JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IAggregatorSource<TQuerySession>, IAggregator<TDoc, TId, TQuerySession>, IInlineProjection<TOperations> 
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Func<IEvent,TId> _identitySource;
    private readonly Func<StreamAction, TId> _streamActionSource;
    

    protected JasperFxSingleStreamProjectionBase(Type[] transientExceptionTypes) : base(AggregationScope.SingleStream, transientExceptionTypes)
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

    IAggregator<T, TIdentity, TQuerySession> IAggregatorSource<TQuerySession>.Build<T, TIdentity>()
    {
        return this.As<IAggregator<T, TIdentity, TQuerySession>>();
    }

    private class NulloIdentitySetter<TDoc, TId> : IIdentitySetter<TDoc, TId>
    {
        public void SetIdentity(TDoc document, TId identity)
        {
            // Nothing
        }
    }

    public async ValueTask<TDoc> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot, CancellationToken cancellation)
    {
        if (!events.Any()) return snapshot;
        
        // get the id off of the event
        var action = await DetermineActionAsync(session, snapshot, _identitySource(events[0]), new NulloIdentitySetter<TDoc, TId>(), events, cancellation);
        
        // TODO -- what the heck to do here if it's null?
        
        return action.Snapshot;
    }

    public async ValueTask<TDoc> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot, TId id,
        IIdentitySetter<TDoc, TId> identitySetter,
        CancellationToken cancellation)
    {
        if (!events.Any()) return snapshot;
        
        // get the id off of the event
        var action = await DetermineActionAsync(session, snapshot, id, identitySetter, events, cancellation);
        
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
        
        // TODO -- end the optimization here. Done more harm than good. Duplication is worse
        
        if (streams.Count == 1)
        {
            var stream = streams[0];
            var storage = await session.FetchProjectionStorageAsync<TDoc, TId>(stream.TenantId, cancellation);
            var id = _streamActionSource(stream);
            var snapshot = stream.ActionType == StreamActionType.Start ? default : await storage.LoadAsync(id, cancellation);

            var tenantedSession = session.CorrectSessionForTenancy<TQuerySession>(stream.TenantId);
            
            var action = await DetermineActionAsync(tenantedSession, snapshot, id, storage, stream.Events, cancellation);

            // TODO -- might want to log a Debug warning here. Introduce some logic here for the duplication!
            if (action.Snapshot == null && action.Type != ActionType.Delete && action.Type != ActionType.HardDelete) return;
            
            storage.ApplyInline(action, id, stream.TenantId);
            
            maybeArchiveStream(storage, stream, id);
            
            return;
        }
        
        var groups = streams.GroupBy(x => x.TenantId).ToArray();
        foreach (var group in groups)
        {
            var storage = await session.FetchProjectionStorageAsync<TDoc, TId>(group.Key, cancellation);
            var ids = group.Where(x => x.ActionType == StreamActionType.Append).Select(x => _streamActionSource(x)).ToArray();
            
            var snapshots = await storage.LoadManyAsync(ids, cancellation);
            foreach (var stream in group)
            {
                var id = _streamActionSource(stream);
                snapshots.TryGetValue(id, out var snapshot);
                
                var tenantedSession = session.CorrectSessionForTenancy<TQuerySession>(stream.TenantId);

                var action = await DetermineActionAsync(tenantedSession, snapshot, id, storage, stream.Events, cancellation);
                
                // TODO -- might want to log a Debug warning here
                if (action.Snapshot == null && action.Type != ActionType.Delete && action.Type != ActionType.HardDelete) continue;
                
                storage.ApplyInline(action, id, stream.TenantId);
                
                maybeArchiveStream(storage, stream, id);
            }
        }
    }
    
    private void maybeArchiveStream(IProjectionStorage<TDoc, TId> storage, StreamAction action, TId id)
    {
        if (Scope == AggregationScope.SingleStream && action.Events.OfType<IEvent<Archived>>().Any())
        {
            storage.ArchiveStream(id, action.TenantId);
        }
    }
}