using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

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

    public TenancyGrouping TenancyGrouping { get; private set; } = TenancyGrouping.RespectTenant;

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

    async Task IInlineProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
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
                storage.ApplyInline(finalSnapshot, action, slice.Id, group.TenantId);

                if (operations.EnableSideEffectsOnInlineProjections)
                {
                    await RaiseSideEffects(operations, slice);
                    if (slice.RaisedEvents != null)
                    {
                        throw new InvalidOperationException(
                            "Events cannot be appended in projection side effects from Inline projections");
                    }

                    if (slice.PublishedMessages != null)
                    {
                        var sink = await operations.GetOrStartMessageSink().ConfigureAwait(false);
                        foreach (var message in slice.PublishedMessages)
                        {
                            await sink.PublishAsync(message, slice.TenantId).ConfigureAwait(false);
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
    public void CustomGrouping(Func<TQuerySession, IEnumerable<IEvent>, IEventGrouping<TId>, Task> func)
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }
        
        _defaultSlicer.CustomGrouping(func);
    }

    /// <summary>
    /// If your grouping of events to aggregates doesn't fall into any simple pattern supported
    /// directly by MultiStreamProjection, supply your own "let me do whatever I want" event slicer
    /// </summary>
    /// <param name="slicer"></param>
    [Obsolete("This can be accomplished by using the overload that takes in a lambda. See the documentation")]
    public void CustomGrouping(IEventSlicer<TDoc, TId, TQuerySession> slicer)
    {
        _customSlicer = slicer;
    }
    
}