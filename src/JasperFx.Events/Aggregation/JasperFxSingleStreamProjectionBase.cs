using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

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
/// Concretely, as of Marten 9.23 / Polecat 5.12. Polecat's and Fisher's
/// <c>SingleStreamProjection&lt;TDoc,TId&gt;</c> are empty class bodies, so nothing is lost there. Marten's
/// is not — it adds two behaviors, one of which has since been resolved here:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b><c>BuildSlicer</c></b> — <b>resolved as of jasperfx#723.</b> This base now sets
///     <c>ForceSingleTenancy</c> itself, from <see cref="IEventTenancySource" /> on the session, so the
///     wolverine#2053 fix no longer depends on which subclass you derive from. Marten's override, where
///     still present, computes the same answer. A session that does not implement the seam yields the
///     pre-#723 behavior.
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
/// The surviving one — <c>UseVersionFromMatchingStream</c> — is the kind of divergence that produces a
/// wrong answer rather than an error, and is invisible until something downstream is already wrong, which
/// is why this warning stays here rather than being left to be rediscovered. It is not hoisted because
/// doing so needs a shared document-mapping abstraction, which
/// <see href="https://github.com/JasperFx/jasperfx/issues/647" /> deliberately declined to build.
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

    /// <summary>
    /// A single stream projection always applies to <see cref="Archived" />, whether or not the
    /// aggregate declares anything for it — jasperfx#778.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate outside the one jasperfx#780 closed</b>, and on a store where it bites, closing that
    /// one alone changes nothing. <c>Archived</c> carries no state, so an aggregate has no reason to
    /// declare an <c>Apply</c> for it — which leaves it out of <c>AllEventTypes</c>, so
    /// <c>AppliesTo</c> answers false, <c>ApplyInline</c>'s opening
    /// <c>streams.Where(AppliesTo(...))</c> screens the whole stream out before reading anything, and
    /// the async shard's own event filter never delivers the event into a slice at all.
    /// </para>
    /// <para>
    /// Verified on Fisher, whose <c>StreamArchivingCompliance</c> still failed both archived-event
    /// facts against jasperfx#780 alone and passes both with this.
    /// </para>
    /// <para>
    /// Only the single stream scope, because only it archives: <c>maybeArchiveStream</c> returns
    /// immediately for any other scope, so widening a multi stream projection's event types would hand
    /// its shard events it has nothing to do with.
    /// </para>
    /// <para>
    /// Widening what the projection <em>sees</em> is not widening what it <em>archives</em>. Ownership
    /// is still the marten#4093 rule — a snapshot present before or after the slice — so a sibling
    /// projection in a composite that does not own the stream now sees the event and still declines.
    /// </para>
    /// <para>
    /// ⚠️ <b>An empty set is left empty</b>, and skipping that guard is a real regression rather than a
    /// tidiness point: <c>AppliesTo</c> reads an empty <c>AllEventTypes</c> as "applies to everything",
    /// which is how a catch-all <c>Evolve(IEvent)</c> projection declares itself. Adding
    /// <c>Archived</c> unconditionally turns that "everything" into "exactly one type", and such a
    /// projection then sees nothing at all — seven of
    /// <c>SelfAggregatingEvolveCompliance</c>'s facts, caught by the first cut of this override.
    /// </para>
    /// </remarks>
    protected override Type[] determineEventTypes()
    {
        var types = base.determineEventTypes();

        return types.Length == 0 ? types : [.. types, typeof(Archived)];
    }

    // ForceSingleTenancy is the wolverine#2053 / marten#4085 fix: on a single-tenanted store, events whose
    // tenant_id values disagree must still fold into one aggregate. Marten used to be the only store that
    // set it, by overriding this method -- so a consumer deriving from THIS type, and both other stores
    // whose subclasses are empty class bodies, silently went without it. Resolving it here from the session
    // closes that gap for every store at once. See jasperfx#723.
    //
    // A session that does not implement IEventTenancySource yields false, which is exactly the behavior
    // this method had before the seam existed.
    public override IEventSlicer BuildSlicer(TQuerySession session)
    {
        return new TenantedEventSlicer<TDoc, TId>(new ByStream<TDoc, TId>())
        {
            ForceSingleTenancy = session is IEventTenancySource source
                                 && source.EventTenancyStyle == TenancyStyle.Single
        };
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
                
                // Ownership is signalled by a pre-loaded snapshot OR a materialized one from the
                // slice. In a composite projection with multiple single-stream children, sibling
                // projections that do not own this stream skip the archive. See marten#4093.
                var ownsStream = snapshot != null || transformed != null;

                if (transformed != null || action == ActionType.Delete || action == ActionType.HardDelete)
                {
                    storage.ApplyInline(transformed, action, id, stream.TenantId);

                    if (session.EnableSideEffectsOnInlineProjections)
                    {
                        await processSideEffectMessages(session, id, stream, transformed).ConfigureAwait(false);
                    }
                }

                // Deliberately NOT inside the block above, and this is the whole of marten#5343's
                // archiving finding: an Archived event appended on its own -- with no other event the
                // aggregate handles in the same batch -- leaves transformed null, so the early
                // `continue` this replaces skipped archival entirely and the stream stayed live.
                // Ownership does not depend on the aggregate having CHANGED; a pre-loaded snapshot
                // establishes it on its own, which is what the `snapshot != null` disjunct above was
                // always for. Under the old shape that disjunct was unreachable -- dead code
                // documenting the intent the `continue` defeated.
                maybeArchiveStream(storage, stream, id, ownsStream: ownsStream);
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