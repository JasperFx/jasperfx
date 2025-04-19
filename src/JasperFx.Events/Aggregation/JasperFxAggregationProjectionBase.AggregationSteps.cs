using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    public IAggregationSteps<TDoc, TQuerySession> CreateEvent<TEvent>(Func<TEvent, TDoc> creator) where TEvent : class
    {
        _application.CreateEvent(creator);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> CreateEvent<TEvent>(Func<TEvent, TQuerySession, Task<TDoc>> creator)
        where TEvent : class
    {
        _application.CreateEvent(creator);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>() where TEvent : class
    {
        DeleteEvents.Add(typeof(TEvent));
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>(Func<TEvent, bool> shouldDelete)
        where TEvent : class
    {
        _application.DeleteEvent(shouldDelete);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>(Func<TDoc, TEvent, bool> shouldDelete)
        where TEvent : class
    {
        _application.DeleteEvent(shouldDelete);
        return this;
    }


    public IAggregationSteps<TDoc, TQuerySession> DeleteEventAsync<TEvent>(
        Func<TQuerySession, TDoc, TEvent, Task<bool>> shouldDelete) where TEvent : class
    {
        _application.DeleteEventAsync(shouldDelete);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Func<TDoc, TEvent, TDoc> handler)
        where TEvent : class
    {
        _application.ProjectEvent(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Func<TDoc, TDoc> handler) where TEvent : class
    {
        _application.ProjectEvent<TEvent>(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEvent<TEvent>(Action<TQuerySession, TDoc, TEvent> handler)
        where TEvent : class
    {
        _application.ProjectEvent(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEventAsync<TEvent>(
        Func<TQuerySession, TDoc, TEvent, Task<TDoc>> handler) where TEvent : class
    {
        _application.ProjectEvent(handler);
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
        _application.ProjectEvent(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> ProjectEventAsync<TEvent>(
        Func<TQuerySession, TDoc, TEvent, Task> handler) where TEvent : class
    {
        _application.ProjectEvent(handler);
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> TransformsEvent<TEvent>() where TEvent : class
    {
        TransformedEvents.Add(typeof(TEvent));
        return this;
    }

}