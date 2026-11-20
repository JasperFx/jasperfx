using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: inherits aggregation method discovery from JasperFxAggregationProjectionBase; reflective handler lookup propagates RUC. Aggregate/Id types preserved by registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic method parameter receives Type values obtained reflectively via inherited helpers. TDoc/TId preserved at registration.")]
public abstract class JasperFxMultiStreamProjectionBase<TDoc, TId, TOperations, TQuerySession> :
    JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IInlineProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    private readonly EventSlicer<TDoc, TId, TQuerySession> _defaultSlicer = new();
    private IEventSlicer<TDoc, TId, TQuerySession>? _customSlicer;

    protected JasperFxMultiStreamProjectionBase() : base(AggregationScope.MultiStream)
    {
        Name = typeof(TDoc).Name;
    }

    public TenancyGrouping TenancyGrouping { get; set; } = TenancyGrouping.RespectTenant;

    /// <summary>
    /// A multi-stream projection can join a composite single-pass rebuild only when it uses the default
    /// id-based slicing and standard per-tenant grouping. A custom slicer or tenant-rollup grouping does
    /// not fan cleanly into one ordered pass, so such projections opt out initially (jasperfx#407 Phase A).
    /// </summary>
    public override bool CanParticipateInCompositeReplay
        => _customSlicer == null && TenancyGrouping != TenancyGrouping.RollUpByTenant;

    public sealed override void AssembleAndAssertValidity()
    {
        base.AssembleAndAssertValidity();

        if (TenancyGrouping == TenancyGrouping.RollUpByTenant) return;

        if (_customSlicer == null && !_defaultSlicer.HasAnyRules())
        {
            throw new InvalidProjectionException(
                $"{GetType().FullNameInCode()} is a multi-stream projection, but has no defined event slicing rules.");
        }
    }

    protected override IInlineProjection<TOperations> buildForInline()
    {
        return this;
    }

    public override IEventSlicer BuildSlicer(TQuerySession session)
    {
        switch (TenancyGrouping)
        {
            case TenancyGrouping.RespectTenant:
                return new TenantedEventSlicer<TDoc, TId, TQuerySession>(session, _customSlicer ?? _defaultSlicer);
            case TenancyGrouping.AcrossTenants:
                return new AcrossTenantSlicer<TDoc, TId, TQuerySession>(session, _customSlicer ?? _defaultSlicer);
            case TenancyGrouping.RollUpByTenant:
                if (typeof(TId) == typeof(string))
                {
                    return new TenantRollupSlicer<TDoc>();
                }

                var valueTypeInfo = ValueTypeInfo.ForType(typeof(TId));
                if (valueTypeInfo.SimpleType == typeof(string))
                {
                    return new TenantRollupSlicer<TDoc, TId>();
                }
                
                throw new InvalidOperationException(
                    "The tenant id rollup requires either an identifier of type string or a strong typed identifier that wraps a string");
                
        }

        throw new ArgumentOutOfRangeException();
    }
    
    /// <summary>
    ///     Apply "fan out" operations to the given TEvent type that inserts an enumerable of TChild events right behind the
    ///     parent
    ///     event in the event stream
    /// </summary>
    /// <param name="fanOutFunc"></param>
    /// <param name="mode">Should the fan out operation happen after grouping, or before? Default is after</param>
    /// <typeparam name="TEvent"></typeparam>
    /// <typeparam name="TChild"></typeparam>
    public void FanOut<TEvent, TChild>(Func<TEvent, IEnumerable<TChild>> fanOutFunc,
        FanoutMode mode = FanoutMode.AfterGrouping) where TEvent : notnull where TChild : notnull
    {
        IncludeType<TEvent>();
        _defaultSlicer.FanOut(fanOutFunc, mode);
    }

    /// <summary>
    ///     Apply "fan out" operations to the given IEvent<TEvent> type that inserts an enumerable of TChild events right behind the
    ///     parent
    ///     event in the event stream
    /// </summary>
    /// <param name="fanOutFunc"></param>
    /// <param name="mode">Should the fan out operation happen after grouping, or before? Default is after</param>
    /// <typeparam name="TEvent"></typeparam>
    /// <typeparam name="TChild"></typeparam>
    public void FanOut<TEvent, TChild>(Func<IEvent<TEvent>, IEnumerable<TChild>> fanOutFunc,
        FanoutMode mode = FanoutMode.AfterGrouping) where TEvent : notnull where TChild : notnull
    {
        IncludeType<TEvent>();
        _defaultSlicer.FanOut(fanOutFunc, mode);
    }

    async Task IInlineProjection<TOperations>.ApplyAsync(TOperations operations, IEnumerable<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToArray();
        var slicer = BuildSlicer(operations);
        
        var groups = await slicer.SliceAsync(events);
        foreach (var group in groups.OfType<SliceGroup<TDoc, TId>>())
        {
            var storage = await operations.FetchProjectionStorageAsync<TDoc, TId>(group.TenantId, cancellation);
            var ids = group.Slices.Select(x => x.Id).ToArray();
            
            var snapshots = await storage.LoadManyAsync(ids, cancellation);
            foreach (var slice in group.Slices)
            {
                snapshots.TryGetValue(slice.Id, out var snapshot);
                var (finalSnapshot, action) = await DetermineActionAsync(operations, snapshot, slice.Id, storage, slice.Events(), cancellation);

                (_, finalSnapshot) = tryApplyMetadata(slice.Events(), finalSnapshot, slice.Id, storage);
                
                storage.ApplyInline(finalSnapshot, action, slice.Id, group.TenantId);

                slice.Snapshot = finalSnapshot;
                
                if (operations.EnableSideEffectsOnInlineProjections)
                {
                    await RaiseSideEffects(operations, slice.Id, slice);
                    if (slice.RaisedEvents != null)
                    {
                        throw new InvalidOperationException(
                            "Events cannot be appended in projection side effects from Inline projections");
                    }

                    if (slice.PublishedMessages != null)
                    {
                        var sink = await operations.GetOrStartMessageSink().ConfigureAwait(false);
                        await slice.PublishMessagesAsync(sink).ConfigureAwait(false);
                    }

                    // Independent path: messages enqueued with per-message metadata.
                    if (slice.PublishedMessagesWithMetadata != null)
                    {
                        var sink = await operations.GetOrStartMessageSink().ConfigureAwait(false);
                        foreach (var (message, metadata) in slice.PublishedMessagesWithMetadata)
                        {
                            await sink.PublishAsync(message, metadata).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Group events by the tenant id. Use this option if you need to do roll up summaries by
    /// tenant id within a conjoined multi-tenanted event store.
    /// </summary>
    public void RollUpByTenant()
    {
        if (typeof(TId) != typeof(string))
        {
            var valueIdType = ValueTypeInfo.ForType(typeof(TId));
            if (valueIdType.SimpleType != typeof(string))
            {
                throw new InvalidOperationException("Rolling up by Tenant Id requires the identity type to be string or a value type whose 'simple' type is string");
            }
        }

        TenancyGrouping = TenancyGrouping.RollUpByTenant;
    }
    
    /// <summary>
    /// marten#6003: how the daemon finds one identity's events for this projection, judged from its grouping rules and
    /// whether it keeps a slice index. <see cref="IdentityFromStream{TEvent}" /> and <see cref="IdentityFromTag{TTag}" />
    /// are exact, and so is any grouping with <see cref="AsyncOptions.UseSliceIndex()" />. A plain
    /// <c>Identity</c>/<c>Identities</c> delegate, custom grouping, a custom slicer, grouping across tenants or rolling
    /// up by tenant make it <see cref="IdentityAccessClass.ScanOnly" /> without the index.
    /// </summary>
    public override IdentityAccess IdentityAccess
    {
        get
        {
            if (TenancyGrouping == TenancyGrouping.RollUpByTenant)
            {
                return IdentityAccess.ScanOnly(
                    "RollUpByTenant() makes the tenant the identity, and a tenant's events can only be found by scanning them.");
            }

            if (Options.SliceIndex is { } sliceIndex)
            {
                if (_customSlicer == null && _defaultSlicer.HasLookupGrouping && _defaultSlicer.HasBeforeGroupingFanOut)
                {
                    return IdentityAccess.ScanOnly(
                        "A custom grouping combined with a fan-out before grouping can't be replayed from the slice index: the index records the parent event, and only the custom grouping knows which of its children went where.");
                }

                var custom = _customSlicer != null || _defaultSlicer.HasLookupGrouping;
                var grouping = !custom
                    ? "replays slice its indexed events again"
                    : sliceIndex.CustomGroupingReplay == SliceIndexGroupingReplay.AsRecorded
                        ? "replays give an identity the events the index recorded for it, without running the custom grouping again"
                        : "replays run the custom grouping again over the indexed events (RerunCustomGroupingOnReplay())";

                return IdentityAccess.SliceIndex(
                    $"The slice index (UseSliceIndex(), retention {sliceIndex.Retention}) records which identities each event went to; {grouping}.");
            }

            if (TenancyGrouping == TenancyGrouping.AcrossTenants)
            {
                return IdentityAccess.ScanOnly(
                    "Grouping across tenants puts every tenant's events in one identity, so its events can't be read by stream or tag. Turn on the slice index with UseSliceIndex().");
            }

            if (_customSlicer != null)
            {
                return IdentityAccess.ScanOnly(
                    $"The custom slicer {_customSlicer.GetType().FullNameInCode()} can't be inverted to find one identity's events. Turn on the slice index with UseSliceIndex().");
            }

            return _defaultSlicer.IdentityAccess;
        }
    }

    internal override IdentitySourcePlan IdentitySources
    {
        get
        {
            var access = IdentityAccess;
            var acrossTenants = TenancyGrouping == TenancyGrouping.AcrossTenants;

            switch (access.Class)
            {
                case IdentityAccessClass.SliceIndex:
                    var custom = _customSlicer != null || _defaultSlicer.HasLookupGrouping;
                    var asRecorded = custom && Options.SliceIndex!.CustomGroupingReplay == SliceIndexGroupingReplay.AsRecorded;
                    return new IdentitySourcePlan(access, false, [], true, asRecorded, acrossTenants);

                case IdentityAccessClass.Stream:
                case IdentityAccessClass.Tag:
                    return new IdentitySourcePlan(access, _defaultSlicer.Rules.HasStreamRules,
                        _defaultSlicer.Rules.TagTypes, false, false, false);

                default:
                    return new IdentitySourcePlan(access, false, [], false, false, acrossTenants);
            }
        }
    }

    internal override async ValueTask<EventSlice<TDoc, TId>?> SliceForIdentityAsync(TQuerySession session,
        string tenantId, TId id, IReadOnlyList<IEvent> candidates, bool asRecorded)
    {
        if (asRecorded)
        {
            // The index is the grouping: these are the identity's events. Fan-out after grouping runs again, since the
            // index recorded the parent event.
            var recorded = new EventSlice<TDoc, TId>(id, tenantId, candidates);
            if (_customSlicer == null) _defaultSlicer.ApplyAfterGroupingFanOut(recorded);
            return recorded;
        }

        var groups = await BuildSlicer(session).SliceAsync(candidates).ConfigureAwait(false);
        var groupTenant = TenancyGrouping == TenancyGrouping.AcrossTenants ? StorageConstants.DefaultTenantId : tenantId;

        foreach (var group in groups.OfType<SliceGroup<TDoc, TId>>())
        {
            if (group.TenantId == groupTenant && group.Slices.TryFind(id, out var slice)) return slice;
        }

        return null;
    }

    /// <summary>
    /// marten#6003: group each <typeparamref name="TEvent" /> by the stream it was appended to: the identity is the
    /// event's stream id or key, wrapped when the projection's identity is a strong-typed identifier. Unlike
    /// <c>Identity&lt;TEvent&gt;(e =&gt; e.StreamId)</c>, the daemon can find one identity's events again from this
    /// rule alone, so the projection's <see cref="IdentityAccess" /> is exact.
    /// </summary>
    public void IdentityFromStream<TEvent>() where TEvent : notnull
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }

        _defaultSlicer.IdentityFromStream<TEvent>();
    }

    /// <summary>
    /// marten#6003: group every event that carries a stored DCB tag of type <typeparamref name="TTag" /> by the tag's
    /// value: the identity is the tag itself, or the simple value it wraps. An event tagged with several values of the
    /// type goes to each of them. The daemon can find one identity's events again through the tag, so the projection's
    /// <see cref="IdentityAccess" /> is exact. Declared, never inferred: a plain <c>Identity</c> delegate that happens to
    /// pick the same value as a <c>TagWith</c> rule is still a delegate.
    /// </summary>
    public void IdentityFromTag<TTag>() where TTag : notnull
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }

        _defaultSlicer.IdentityFromTag<TTag>();

        // The daemon's loader only reads the events' tags for a projection that slices by them
        IncludeEventTags = true;
    }

    public void Identity<TEvent>(Func<TEvent, TId> identityFunc) where TEvent : notnull
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }
        
        _defaultSlicer.Identity(identityFunc);
    }
    
    public void Identities<TEvent>(Func<TEvent, IReadOnlyList<TId>> identitiesFunc) where TEvent : notnull
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }
        
        _defaultSlicer.Identities(identitiesFunc);
    }
    
    /// <summary>
    ///     Apply a custom event grouping strategy for events. This is additive to Identity() or Identities()
    /// </summary>
    /// <param name="grouper"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void CustomGrouping(IJasperFxAggregateGrouper<TId, TQuerySession> grouper)
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }
        
        _defaultSlicer.CustomGrouping(grouper);
    }
    
    /// <summary>
    ///     Apply a custom event grouping strategy for events. This is additive to Identity() or Identities()
    /// </summary>
    /// <param name="func"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void CustomGrouping(Func<TQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task> func)
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }
        
        _defaultSlicer.CustomGrouping(func);
    }

}
