using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Grouping;
using JasperFx.Events.NewStuff;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

public abstract class AggregationProjectionBase<TDoc, TId, TOperations, TQuerySession> 
    : ProjectionBase, IAggregationSteps<TDoc, TQuerySession>, IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession>, IAggregationProjection<TDoc, TId, TOperations> where TOperations : TQuerySession
{
    // TODO -- this should be somewhere else
    
    public AggregationScope Scope { get; }
    private readonly AggregateApplication<TDoc,TQuerySession> _application;
    private readonly Lazy<Type[]> _allEventTypes;
    private readonly AggregateVersioning<TDoc,TQuerySession> _versioning;

    protected AggregationProjectionBase(AggregationScope scope)
    {
        Scope = scope;
        ProjectionName = typeof(TDoc).NameInCode();
        _application = new AggregateApplication<TDoc, TQuerySession>(this);

        Options.DeleteViewTypeOnTeardown<TDoc>();

        _allEventTypes = new Lazy<Type[]>(determineEventTypes);

        _versioning = new AggregateVersioning<TDoc, TQuerySession>(scope){Inner = _application};

        RegisterPublishedType(typeof(TDoc));

        if (typeof(TDoc).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            ProjectionVersion = att.Version;
        }
    }
    
    public Type IdentityType { get; } = typeof(TId);
    
    protected virtual Type[] determineEventTypes()
    {
        var eventTypes = _application.AllEventTypes()
            .Concat(DeleteEvents).Concat(TransformedEvents).Distinct().ToArray();
        return eventTypes;
    }
    
    internal IList<Type> DeleteEvents { get; } = new List<Type>();
    internal IList<Type> TransformedEvents { get; } = new List<Type>();
    
    /// <summary>
    /// Template method that is called on the last event in a slice of events that
    /// are updating an aggregate. This was added specifically to add metadata like "LastModifiedBy"
    /// from the last event to an aggregate with user-defined logic. Override this for your own specific logic
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="lastEvent"></param>
    public virtual TDoc ApplyMetadata(TDoc snapshot, IEvent lastEvent)
    {
        return snapshot;
    }
    
    public bool AppliesTo(IEnumerable<Type> eventTypes)
    {
        return eventTypes
            .Intersect(AllEventTypes).Any() || eventTypes.Any(type => AllEventTypes.Any(type.CanBeCastTo));
    }

    public Type[] AllEventTypes => _allEventTypes.Value;

    public bool MatchesAnyDeleteType(IEventSlice slice)
    {
        return slice.Events().Select(x => x.EventType).Intersect(DeleteEvents).Any();
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
    
    public IAggregationSteps<TDoc, TQuerySession> CreateEvent<TEvent>(Func<TEvent, TDoc> creator) where TEvent : class
    {
        _application.CreateEvent<TEvent>(creator);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> CreateEvent<TEvent>(Func<TEvent, TQuerySession, Task<TDoc>> creator) where TEvent : class
    {
        _application.CreateEvent<TEvent>(creator);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>() where TEvent : class
    {
        DeleteEvents.Add(typeof(TEvent));
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>(Func<TEvent, bool> shouldDelete) where TEvent : class
    {
        _application.DeleteEvent<TEvent>(shouldDelete);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>(Func<TDoc, TEvent, bool> shouldDelete) where TEvent : class
    {
        _application.DeleteEvent<TEvent>(shouldDelete);
        return this;
    }


    public IAggregationSteps<TDoc, TQuerySession> DeleteEventAsync<TEvent>(
        Func<TQuerySession, TDoc, TEvent, Task<bool>> shouldDelete) where TEvent : class
    {
        _application.DeleteEventAsync<TEvent>(shouldDelete);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Func<TDoc, TEvent, TDoc> handler) where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Func<TDoc, TDoc> handler) where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Action<TQuerySession, TDoc, TEvent> handler) where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEventAsync<TEvent>(Func<TQuerySession, TDoc, TEvent, Task<TDoc>> handler) where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Action<TDoc> handler)
        where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Action<TDoc, TEvent> handler)
        where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }
    
    public IAggregationSteps<TDoc, TQuerySession> ProjectEventAsync<TEvent>(Func<TQuerySession, TDoc, TEvent, Task> handler) where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> TransformsEvent<TEvent>() where TEvent : class
    {
        TransformedEvents.Add(typeof(TEvent));
        return this;
    }

    public Type ProjectionType => GetType();
    
    // TODO -- rename these? Or leave them alone?
    public string Name => ProjectionName!;
    public uint Version => ProjectionVersion;
    
    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        // TODO -- this *will* get fancier if we do the async projection sharding
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, new ShardName(Name), this, this)
        ];
    }

    public virtual bool TryBuildReplayExecutor(IEventStorage<TOperations, TQuerySession> store, IEventDatabase database, out IReplayExecutor executor)
    {
        // TODO -- overwrite in SingleStreamProjection in Marten
        executor = default;
        return false;
    }

    public IInlineProjection<TOperations> BuildForInline()
    {
        throw new NotImplementedException();
    }

    public ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
    {
        
        var logger = loggerFactory.CreateLogger(GetType());
        
        // TODO -- build the slicer differently
        // TODO -- build the SliceBehavior differently for multi-stream
        var slicer = new TenantedEventSlicer<TDoc, TId>(new ByStream<TDoc, TId>());
        
        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(storage, database, this,
                SliceBehavior.Preprocess, slicer);

        return new GroupedProjectionExecution(shardName, runner, logger);
    }

    public virtual ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice)
    {
        return new ValueTask();
    }

    // TODO -- allow for explicit code
    public virtual async ValueTask<SnapshotAction<TDoc>> ApplyAsync(IProjectionStorage<TDoc, TOperations> storage,
        TDoc? snapshot,
        TId identity, 
        IReadOnlyList<IEvent> events, 
        CancellationToken cancellation)
    {
        // Does the aggregate already exist before the events are applied?
        var exists = snapshot != null;

        foreach (var @event in events)
        {
            if (@event is IEvent<Archived>) break;

            try
            {
                if (snapshot == null)
                {
                    snapshot = await _application.Create(@event, storage.Operations, cancellation).ConfigureAwait(false);
                }
                else
                {
                    snapshot = await _application.ApplyAsync(snapshot, @event, storage.Operations, cancellation).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // Should the exception be passed up for potential
                // retries?
                if (storage.IsExceptionTransient(e)) throw;

                throw new ApplyEventException(@event, e);
            }
        }

        if (snapshot == null) return exists ? new Delete<TDoc>(snapshot) : new Nothing<TDoc>(snapshot);

        return new Store<TDoc>(snapshot);
    }
}