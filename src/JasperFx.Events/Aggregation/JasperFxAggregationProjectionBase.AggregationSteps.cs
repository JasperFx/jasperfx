using JasperFx.Events.Daemon;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession> where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    public IAggregationSteps<TDoc, TQuerySession> DeleteEvent<TEvent>() where TEvent : class
    {
        DeleteEvents.Add(typeof(TEvent));
        return this;
    }

    public IAggregationSteps<TDoc, TQuerySession> TransformsEvent<TEvent>() where TEvent : class
    {
        TransformedEvents.Add(typeof(TEvent));
        return this;
    }
}
