using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    : ProjectionBase, IAggregateProjection, IAggregationSteps<TDoc, TQuerySession>,
        IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession>,
        IAggregationProjection<TDoc, TId, TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly Lazy<Type[]> _allEventTypes;
    private readonly AggregateApplication<TDoc, TQuerySession> _application;

    private readonly object _cacheLock = new();
    private readonly List<Type> _transientExceptionTypes = new();
    private readonly AggregateVersioning<TDoc, TQuerySession> _versioning;
    private ImHashMap<string, IAggregateCache<TId, TDoc>> _caches = ImHashMap<string, IAggregateCache<TId, TDoc>>.Empty;
    

    protected JasperFxAggregationProjectionBase(AggregationScope scope, Type[] transientExceptionTypes)
    {
        _transientExceptionTypes.AddRange(transientExceptionTypes);
        Scope = scope;
        ProjectionName = typeof(TDoc).NameInCode();

        // We'll use this to validate even if it's not used at runtime
        _application = new AggregateApplication<TDoc, TQuerySession>(this);
        
        // TODO -- add a helper method here in ReflectionExtensions. Something like "OverriddenByThisType(methodName)
        if (GetType().GetMethod(nameof(Evolve)).DeclaringType == GetType())
        {
            _evolve = evolveDefault;
        }
        else if (GetType().GetMethod(nameof(EvolveAsync)).DeclaringType == GetType())
        {
            _evolve = evolveDefaultAsync;
        }
        else
        {
            _evolve = evolveDefaultAsync;
        }
        
        Options.DeleteViewTypeOnTeardown<TDoc>();

        _allEventTypes = new Lazy<Type[]>(determineEventTypes);

        _versioning = new AggregateVersioning<TDoc, TQuerySession>(scope) { Inner = _application };

        RegisterPublishedType(typeof(TDoc));

        if (typeof(TDoc).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            ProjectionVersion = att.Version;
        }
    }

    internal IList<Type> DeleteEvents { get; } = new List<Type>();
    internal IList<Type> TransformedEvents { get; } = new List<Type>();
    public AggregationScope Scope { get; }

    public Type AggregateType => typeof(TDoc);
    public Type IdentityType => typeof(TId);

    public Type[] AllEventTypes => _allEventTypes.Value;

    /// <summary>
    ///     Template method that is called on the last event in a slice of events that
    ///     are updating an aggregate. This was added specifically to add metadata like "LastModifiedBy"
    ///     from the last event to an aggregate with user-defined logic. Override this for your own specific logic
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="lastEvent"></param>
    public virtual TDoc ApplyMetadata(TDoc snapshot, IEvent lastEvent)
    {
        return snapshot;
    }

    public bool MatchesAnyDeleteType(IEventSlice slice)
    {
        return slice.Events().Select(x => x.EventType).Intersect(DeleteEvents).Any();
    }

    public virtual ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice)
    {
        return new ValueTask();
    }
    
    public virtual SubscriptionDescriptor Describe()
    {
        return new SubscriptionDescriptor(this,
            Scope == AggregationScope.SingleStream
                ? SubscriptionType.SingleStreamProjection
                : SubscriptionType.MultiStreamProjection);
    }

    public Type ProjectionType => GetType();

    // TODO -- rename these? Or leave them alone?
    public string Name => ProjectionName!;
    public uint Version => ProjectionVersion;

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        // TODO -- this *will* get fancier if we do the async projection sharding
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(Name), this, this)
        ];
    }

    // TODO -- maybe make this implicit, with a call to a virtual
    public virtual bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database,
        out IReplayExecutor executor)
    {
        // TODO -- overwrite in SingleStreamProjection in Marten
        executor = default;
        return false;
    }

    // TODO -- make this implicit
    public abstract IInlineProjection<TOperations> BuildForInline();

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(
        IEventStorage<TOperations, TQuerySession> storage,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());

        // TODO -- may need to track the disposable of the session here
        var session = storage.OpenSession(database);
        var slicer = buildSlicer(session);

        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(storage, database, this,
                SliceBehavior.Preprocess, slicer);

        return new GroupedProjectionExecution(shardName, runner, logger);
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(
        IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        // TODO -- may need to track the disposable of the session here
        var session = storage.OpenSession(database);
        var slicer = buildSlicer(session);

        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(storage, database, this,
                SliceBehavior.Preprocess, slicer);

        return new GroupedProjectionExecution(shardName, runner, logger);
    }

    public IAggregateCache<TId, TDoc> CacheFor(string tenantId)
    {
        if (_caches.TryFind(tenantId, out var cache))
        {
            return cache;
        }

        lock (_cacheLock)
        {
            if (_caches.TryFind(tenantId, out cache))
            {
                return cache;
            }

            cache = Options.CacheLimitPerTenant == 0
                ? new NulloAggregateCache<TId, TDoc>()
                : new RecentlyUsedCache<TId, TDoc> { Limit = Options.CacheLimitPerTenant };

            _caches = _caches.AddOrUpdate(tenantId, cache);

            return cache;
        }
    }


    protected virtual Type[] determineEventTypes()
    {
        var eventTypes = _application.AllEventTypes()
            .Concat(DeleteEvents).Concat(TransformedEvents).Distinct().ToArray();
        return eventTypes;
    }

    public bool AppliesTo(IEnumerable<Type> eventTypes)
    {
        return eventTypes
            .Intersect(AllEventTypes).Any() || eventTypes.Any(type => AllEventTypes.Any(type.CanBeCastTo));
    }

    /// <summary>
    ///     When used as an asynchronous projection, this opts into
    ///     only taking in events from streams explicitly marked as being
    ///     the aggregate type for this projection. Only use this if you are explicitly
    ///     marking streams with the aggregate type on StartStream()
    /// </summary>
    [JasperFxIgnore]
    public void FilterIncomingEventsOnStreamType()
    {
        StreamType = typeof(TDoc);
    }

    protected abstract IEventSlicer buildSlicer(TQuerySession session);

    // TODO -- man, it'd be nice to be able to do this with a synchronous method

    // TODO -- unit test this
    protected virtual bool IsExceptionTransient(Exception exception)
    {
        if (_transientExceptionTypes.Any(x => exception.GetType().CanBeCastTo(x)))
        {
            return true;
        }

        return false;
    }
}