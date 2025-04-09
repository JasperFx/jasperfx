using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public abstract class JasperFxMultiStreamProjectionBase<TDoc, TId, TOperations, TQuerySession> :
    JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>, IInlineProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly EventSlicer<TDoc, TId, TQuerySession> _defaultSlicer = new();
    private IEventSlicer<TDoc, TId, TQuerySession>? _customSlicer;

    protected JasperFxMultiStreamProjectionBase(Type[] transientExceptionTypes) : base(AggregationScope.MultiStream, transientExceptionTypes)
    {
        ProjectionName = typeof(TDoc).Name;
    }

    public TenancyGrouping TenancyGrouping { get; private set; } = TenancyGrouping.RespectTenant;

    protected override IInlineProjection<TOperations> buildForInline()
    {
        return this;
    }

    protected override IEventSlicer buildSlicer(TQuerySession session)
    {
        switch (TenancyGrouping)
        {
            case TenancyGrouping.RespectTenant:
                return new TenantedEventSlicer<TDoc, TId, TQuerySession>(session, _customSlicer ?? _defaultSlicer);
            case TenancyGrouping.AcrossTenants:
                return new AcrossTenantSlicer<TDoc, TId, TQuerySession>(session, _customSlicer ?? _defaultSlicer);
            case TenancyGrouping.RollUpByTenant:
                // TODO -- what if the TId isn't string of course?
                if (typeof(TId) != typeof(string))
                    throw new InvalidOperationException(
                        "JasperFx cannot (yet) support strong typed identifiers for the tenant id rollup");
                return new TenantRollupSlicer<TDoc>();
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
        FanoutMode mode = FanoutMode.AfterGrouping)
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
        FanoutMode mode = FanoutMode.AfterGrouping) where TEvent : notnull
    {
        IncludeType<TEvent>();
        _defaultSlicer.FanOut(fanOutFunc, mode);
    }

    async Task IInlineProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToArray();
        var slicer = buildSlicer(operations);
        
        var groups = await slicer.SliceAsync(events);
        foreach (var group in groups.OfType<SliceGroup<TDoc, TId>>())
        {
            var storage = await operations.FetchProjectionStorageAsync<TDoc, TId>(group.TenantId, cancellation);
            var ids = group.Slices.Select(x => x.Id).ToArray();
            
            var snapshots = await storage.LoadManyAsync(ids, cancellation);
            foreach (var slice in group.Slices)
            {
                snapshots.TryGetValue(slice.Id, out var snapshot);
                var action = await DetermineActionAsync(operations, snapshot, slice.Id, storage, slice.Events(), cancellation);
                storage.ApplyInline(action, slice.Id, group.TenantId);
            }
        }
    }
    
    /// <summary>
    /// Group events by the tenant id. Use this option if you need to do roll up summaries by
    /// tenant id within a conjoined multi-tenanted event store.
    /// </summary>
    public void RollUpByTenant()
    {
        // TODO -- watch for strong typed identifiers. Ugh.
        if (typeof(TId) != typeof(string))
            throw new InvalidOperationException("Rolling up by Tenant Id requires the identity type to be string");

        TenancyGrouping = TenancyGrouping.RollUpByTenant;
    }
    
    // TODO -- option to use across tenants too
    public void Identity<TEvent>(Func<TEvent, TId> identityFunc)
    {
        if (_customSlicer != null)
        {
            throw new InvalidOperationException(
                "There is already a custom event slicer registered for this projection");
        }
        
        _defaultSlicer.Identity(identityFunc);
    }
    
    public void Identities<TEvent>(Func<TEvent, IReadOnlyList<TId>> identitiesFunc)
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