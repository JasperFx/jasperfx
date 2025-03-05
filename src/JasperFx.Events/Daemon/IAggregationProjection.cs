using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Options;

namespace JasperFx.Events.Daemon;


public enum AggregationScope
{
    SingleStream,
    MultiStream
}

public interface IAggregationProjection<TDoc, TId, TOperations>
{
    /// <summary>
    /// Use to create "side effects" when running an aggregation (single stream, custom projection, multi-stream)
    /// asynchronously in a continuous mode (i.e., not in rebuilds)
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice);

    AggregationScope AggregationScope { get; }
    
    bool MatchesAnyDeleteType(IEventSlice slice);
    TDoc ApplyMetadata(TDoc aggregate, IEvent @event);
    
    IEventSlicer Slicer { get; }

    ValueTask<SnapshotAction<TDoc>> ApplyAsync(TDoc? snapshot, TId identity, IReadOnlyList<IEvent> events);

    /*
        // Does the aggregate already exist before the events are applied?
       var exists = aggregate != null;

       foreach (var @event in slice.Events())
       {
           if (@event is IEvent<Archived>) break;

           try
           {
               if (aggregate == null)
               {
                   aggregate = await _application.Create(@event, storage.Operations, cancellation).ConfigureAwait(false);
               }
               else
               {
                   aggregate = await _application.ApplyAsync(aggregate, @event, storage.Operations, cancellation).ConfigureAwait(false);
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
     */
}

// TODO -- this should be somewhere else
public abstract class AggregationProjectionBase<TDoc, TQuerySession> : ProjectionBase, IAggregationSteps<TDoc, TQuerySession>
{
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
}