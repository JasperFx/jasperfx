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

    protected sealed override IEventSlicer buildSlicer()
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
        var ids = streams.Select(x => _streamActionSource(x)).ToArray();
        var storage = session.ProjectionStorageFor<TDoc, TId>();

        var snapshots = await storage.LoadManyAsync(ids, cancellation);

        foreach (var stream in streams)
        {
            var id = _streamActionSource(stream);
            if (snapshots.TryGetValue(id, out var snapshot))
            {
                var action = await ApplyAsync(session, snapshot, id, stream.Events, cancellation);
                
                switch (action.Type)
                {
                    case ActionType.Delete:
                        storage.Delete(id);
                        break;
                    case ActionType.Store:
                        storage.Store(snapshot);
                        break;
                    case ActionType.HardDelete:
                        storage.HardDelete(snapshot);
                        break;
                    case ActionType.UnDeleteAndStore:
                        storage.UnDelete(snapshot);
                        storage.Store(snapshot);
                        break;
                }
            }
        }
    }
}