using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public abstract class JasperFxSingleStreamProjectionBase<TDoc, TId, TOperations, TQuerySession> : JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IAggregatorSource<TQuerySession>, IAggregator<TDoc, TId, TQuerySession>, IInlineProjection<TOperations> 
    where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    private readonly Func<IEvent,TId> _identitySource;
    private readonly Func<StreamAction, TId> _streamActionSource;
    

    protected JasperFxSingleStreamProjectionBase() : base(AggregationScope.SingleStream)
    {
        _identitySource = IEvent.CreateAggregateIdentitySource<TId>();
        _streamActionSource = StreamAction.CreateAggregateIdentitySource<TId>();
    }

    // This actually does need to be overridable in Marten
    public override IEventSlicer BuildSlicer(TQuerySession session)
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

    async ValueTask<TDoc?> IAggregator<TDoc, TQuerySession>.BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot, CancellationToken cancellation)
    {
        (snapshot, events) = Compacted<TDoc>.MaybeFastForward(snapshot, events);
        
        if (!events.Any()) return snapshot;
        
        // get the id off of the event
        var id = _identitySource(events[0]);
        var nulloIdentitySetter = new NulloIdentitySetter<TDoc, TId>();
        (snapshot, _) = await DetermineActionAsync(session, snapshot, id, nulloIdentitySetter, events, cancellation);
        (_, snapshot) = tryApplyMetadata(events, snapshot, id, nulloIdentitySetter);
        
        return snapshot;
    }

    async ValueTask<TDoc?> IAggregator<TDoc, TId, TQuerySession>.BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot, TId id,
        IIdentitySetter<TDoc, TId> identitySetter,
        CancellationToken cancellation)
    {
        if (!events.Any()) return snapshot;
        
        // get the id off of the event
        (snapshot, _) = await DetermineActionAsync(session, snapshot, id, identitySetter, events, cancellation);
        (_, snapshot) = tryApplyMetadata(events, snapshot, id, identitySetter);

        return snapshot;
    }

    protected override IInlineProjection<TOperations> buildForInline()
    {
        return this;
    }

    async Task IInlineProjection<TOperations>.ApplyAsync(TOperations session, IEnumerable<StreamAction> streams, CancellationToken cancellation)
    {
        // Screen out any stream that doesn't have any matching events.
        // 9.0 (#4306): the parameter is now IEnumerable<StreamAction>, so we
        // materialize once locally and work with the concrete array.
        var matching = streams.Where(x => AppliesTo(x.Events.Select(e => e.EventType).ToArray())).ToArray();

        if (matching.Length == 0) return;

        var groups = matching.GroupBy(x => x.TenantId).ToArray();
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

                var (transformed, action) = await DetermineActionAsync(tenantedSession, snapshot, id, storage, stream.Events, cancellation);
                
                // Moved out of the application to avoid it getting double called
                (_, transformed) = tryApplyMetadata(stream.Events, transformed, id, storage);
                
                if (transformed == null && action != ActionType.Delete && action != ActionType.HardDelete) continue;

                storage.ApplyInline(transformed, action, id, stream.TenantId);

                // Gate archival on whether this projection owns the stream. Ownership
                // is signalled by a pre-loaded snapshot OR a materialized one from the
                // slice. In a composite projection with multiple single-stream children,
                // sibling projections that do not own this stream skip the archive.
                // See issue JasperFx/marten#4093.
                maybeArchiveStream(storage, stream, id, ownsStream: snapshot != null || transformed != null);

                if (session.EnableSideEffectsOnInlineProjections)
                {
                    await processSideEffectMessages(session, id, stream, transformed).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task processSideEffectMessages(TOperations session, TId id, StreamAction stream, TDoc? transformed)
    {
        var slice = new EventSlice<TDoc, TId>(id, stream.TenantId, stream.Events)
        {
            Snapshot = transformed
        };

        await RaiseSideEffects(session, slice);
        if (slice.RaisedEvents != null)
        {
            throw new InvalidOperationException(
                "Events cannot be appended in projection side effects from Inline projections");
        }

        if (slice.PublishedMessages != null)
        {
            var sink = await session.GetOrStartMessageSink().ConfigureAwait(false);
            foreach (var message in slice.PublishedMessages)
            {
                await sink.PublishAsync(message, stream.TenantId).ConfigureAwait(false);
            }
        }

        // Independent path: messages enqueued with per-message metadata.
        if (slice.PublishedMessagesWithMetadata != null)
        {
            var sink = await session.GetOrStartMessageSink().ConfigureAwait(false);
            foreach (var (message, metadata) in slice.PublishedMessagesWithMetadata)
            {
                await sink.PublishAsync(message, metadata).ConfigureAwait(false);
            }
        }
    }

    private void maybeArchiveStream(IProjectionStorage<TDoc, TId> storage, StreamAction action, TId id, bool ownsStream)
    {
        if (Scope != AggregationScope.SingleStream) return;

        // Only the single-stream projection that actually owns the stream — as signalled
        // by a snapshot being present either before or after the slice is applied —
        // should archive the stream. In a composite projection with multiple single
        // stream children, sibling projections otherwise fire redundant (or phantom)
        // stream-archival operations. See issue JasperFx/marten#4093.
        if (!ownsStream) return;

        if (action.Events.OfType<IEvent<Archived>>().Any())
        {
            storage.ArchiveStream(id, action.TenantId);
        }
    }
}

public class NulloIdentitySetter<TDoc1, TId1> : IIdentitySetter<TDoc1, TId1>
{
    public void SetIdentity(TDoc1 document, TId1 identity)
    {
        // Nothing
    }

    public Type IdType => typeof(TId1);

    public TId1 Identity(TDoc1 document)
    {
        throw new NotSupportedException();
    }
}