using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Shared implementation behind each store's own <c>SingleStreamProjection&lt;TDoc,TId&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Derive from your store's subclass, not from this type.</b> A store's subclass may add behavior
/// that this base deliberately omits, and nothing about taking the base instead fails at compile time —
/// you get a projection that builds, runs, and is subtly wrong. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/649" />.
/// </para>
/// <para>
/// Concretely, as of Marten 9.23 / Polecat 5.12. Polecat's <c>SingleStreamProjection&lt;TDoc,TId&gt;</c>
/// is an empty class body, so nothing is lost there. Marten's is not — it adds two behaviors:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b><c>BuildSlicer</c></b> returns a <c>TenantedEventSlicer</c> with <c>ForceSingleTenancy</c> taken
///     from the store's <c>EventGraph.TenancyStyle</c> — the fix for
///     <see href="https://github.com/JasperFx/wolverine/issues/2053" />. This base returns the same slicer
///     <em>without</em> it, so taking the base changes event slicing on a single-tenanted Marten store.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b><c>IMartenAggregateProjection.ConfigureAggregateMapping</c></b> sets
///     <c>mapping.UseVersionFromMatchingStream = true</c>, which changes how an aggregate's version
///     metadata is persisted. Taking the base silently drops it — which matters most on exactly the
///     documents someone has put optimistic-concurrency guards around.
///     </description>
///   </item>
/// </list>
/// <para>
/// Both are the kind of divergence that produces a wrong answer rather than an error, and both are
/// invisible until something downstream is already wrong, which is why this warning is here rather than
/// left to be rediscovered. The <c>BuildSlicer</c> comment below saying the method "needs to be
/// overridable in Marten" is the other half of the same story: it is a deliberate escape hatch for the
/// store, not a suggestion that the base is equivalent.
/// </para>
/// <para>
/// <b>If you are writing one projection to compile against several stores</b>, the routes that work today
/// are a per-flavour alias bound to each store's own subclass, or — where the document owns its stream —
/// a self-aggregating document registered with <c>Snapshot&lt;T&gt;()</c>, which sidesteps the question
/// entirely because the store then constructs <em>its own</em> subclass. Note the limit on the second:
/// a self-aggregating document has no constructor, so it cannot carry an <c>IncludeType&lt;T&gt;()</c>
/// event allow-list, which matters when several projections slice the same stream.
/// </para>
/// </remarks>
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

    // This actually does need to be overridable in Marten -- and Marten does override it, to set
    // ForceSingleTenancy from the store's TenancyStyle (wolverine#2053). A consumer deriving from THIS type
    // rather than from Marten's subclass gets the slicer below instead, with no compile error and no runtime
    // failure -- just different slicing on a single-tenanted store. See jasperfx#649 and the class remarks.
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
        // 2.0: parameter widened to IEnumerable<StreamAction>; materialize the
        // filtered set into a local array so we can read .Length and iterate it
        // multiple times.
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

        await RaiseSideEffects(session, id, slice);
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