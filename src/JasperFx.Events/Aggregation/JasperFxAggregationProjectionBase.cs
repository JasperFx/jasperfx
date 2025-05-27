using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    : ProjectionBase, IAggregateProjection, IAggregationSteps<TDoc, TQuerySession>,
        IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession>,
        IAggregationProjection<TDoc, TId, TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    private readonly Lazy<Type[]> _allEventTypes;
    private readonly AggregateApplication<TDoc, TQuerySession> _application;
    
    private readonly AggregateVersioning<TDoc, TQuerySession> _versioning;
    private bool _usesConventionalApplication = true;

    protected JasperFxAggregationProjectionBase(AggregationScope scope)
    {
        Scope = scope;
        Name = typeof(TDoc).NameInCode();

        Type = scope == AggregationScope.SingleStream
            ? SubscriptionType.SingleStreamProjection
            : SubscriptionType.MultiStreamProjection;

        // We'll use this to validate even if it's not used at runtime
        _application = new AggregateApplication<TDoc, TQuerySession>(this);
        
        _buildAction = buildActionAsync;
        
        establishBuildActionAndEvolve();
        
        Options.DeleteViewTypeOnTeardown<TDoc>();

        _allEventTypes = new Lazy<Type[]>(determineEventTypes);

        _versioning = new AggregateVersioning<TDoc, TQuerySession>(scope) { Inner = _application };

        RegisterPublishedType(typeof(TDoc));

        if (typeof(TDoc).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            base.Version = att.Version;
        }
    }

    public Type ImplementationType => GetType();
    public SubscriptionType Type { get; }
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];

    private static readonly string[] methodNames = [nameof(DetermineAction), nameof(DetermineActionAsync), nameof(Evolve), nameof(EvolveAsync)];
    private void establishBuildActionAndEvolve()
    {
        if (isOverridden(nameof(DetermineAction)))
        {
            _usesConventionalApplication = false;
            _buildAction = (_, snapshot, id, _, events, _) => new ValueTask<(TDoc?, ActionType)>(DetermineAction(snapshot, id, events));
        }
        else if (isOverridden(nameof(DetermineActionAsync)))
        {
            _usesConventionalApplication = false;
            _buildAction = DetermineActionAsync;
        }
        else if (isOverridden(nameof(Evolve)))
        {
            _usesConventionalApplication = false;
            _evolve = evolveDefault;
        }
        else if (isOverridden(nameof(EvolveAsync)))
        {
            _usesConventionalApplication = false;
            _evolve = evolveDefaultAsync;
        }
        else
        {
            _usesConventionalApplication = true;
            _evolve = evolveDefaultAsync;
        }
    }
    
    private bool isOverridden(string methodName)
    {
        return GetType().GetMethod(methodName).DeclaringType.Assembly != typeof(IEvent).Assembly;
    }


    protected bool IsUsingConventionalMethods => _usesConventionalApplication;
    
    public override void AssembleAndAssertValidity()
    {
        var overrides = methodNames.Where(isOverridden).ToArray();
        switch (overrides.Length)
        {
            case 0:
                _application.AssertValidity();
                break;
            case 1:
                if (_application.HasAnyMethods())
                {
                    throw new InvalidProjectionException(
                        $"This projection can only use the override of '{overrides.Single()}' or conventional Apply/Create/ShouldDelete methods and line lambdas, but not both");
                }

                break;
            case 2:
                throw new InvalidProjectionException("Only one of these methods can be overridden: " +
                                                    overrides.Join(", "));
                
        }

        var eventTypes = determineEventTypes();
        IncludedEventTypes.Fill(eventTypes);
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

    public bool MatchesAnyDeleteType(IReadOnlyList<IEvent> events)
    {
        return events.Select(x => x.EventType).Intersect(DeleteEvents).Any();
    }

    /// <summary>
    /// Potentially raise "side effects" during projection processing to either emit additional events,
    /// or publish messages
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    public virtual ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice)
    {
        return new ValueTask();
    }
    
    public SubscriptionDescriptor Describe(IEventStore store)
    {
        return new SubscriptionDescriptor(this, store);
    }

    public Type ProjectionType => GetType();

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(Name, ShardName.All, Version), this, this)
        ];
    }

    public virtual bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline() => buildForInline();

    protected abstract IInlineProjection<TOperations> buildForInline();

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(
        IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());

        var session = store.OpenSession(database);
        var slicer = BuildSlicer(session);

        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(store, database, this,
                SliceBehavior.Preprocess, slicer, logger);

        return new GroupedProjectionExecution(shardName, runner, logger){Disposables = [session]};
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(
        IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        var session = store.OpenSession(database);
        var slicer = BuildSlicer(session);

        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(store, database, this,
                SliceBehavior.Preprocess, slicer, logger);

        return new GroupedProjectionExecution(shardName, runner, logger){Disposables = [session]};
    }

    protected virtual Type[] determineEventTypes()
    {
        var eventTypes = _application.AllEventTypes()
            .Concat(DeleteEvents).Concat(TransformedEvents).Concat(IncludedEventTypes).Distinct().ToArray();
        return eventTypes;
    }

    public bool AppliesTo(IEnumerable<Type> eventTypes)
    {
        // Have to do this because you don't know if any events catch
        if (!AllEventTypes.Any()) return true;
        
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

    public abstract IEventSlicer BuildSlicer(TQuerySession session);

    void IAggregationProjection<TDoc, TId, TOperations, TQuerySession>.StartBatch()
    {
        // Nothing, this is a hook for something else
    }

    ValueTask IAggregationProjection<TDoc, TId, TOperations, TQuerySession>.EndBatchAsync()
    {
        return new ValueTask();
    }

    (IEvent?, TDoc?) IAggregationProjection<TDoc, TId, TOperations, TQuerySession>.TryApplyMetadata(
        IReadOnlyList<IEvent> events, TDoc? aggregate, TId id, IIdentitySetter<TDoc, TId> identitySetter)
    {
        return tryApplyMetadata(events, aggregate, id, identitySetter);
    }
    
    protected (IEvent?, TDoc?) tryApplyMetadata(
        IReadOnlyList<IEvent> events, 
        TDoc? aggregate,
        TId id,
        IIdentitySetter<TDoc, TId> storage)
    {
        var lastEvent = events.LastOrDefault();
        if (aggregate != null)
        {
            foreach (var @event in events)
            {
                aggregate = ApplyMetadata(aggregate, @event);
            }
            
            storage.SetIdentity(aggregate, id);
            _versioning.TrySetVersion(aggregate, lastEvent);
        }

        return (lastEvent, aggregate);
    }
}