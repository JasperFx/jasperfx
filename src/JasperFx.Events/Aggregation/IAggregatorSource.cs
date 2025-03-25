namespace JasperFx.Events.Aggregation;

public interface IAggregatorSource<TQuerySession>
{
    Type AggregateType { get; }
    IAggregator<T, TQuerySession> Build<T>();
    IAggregator<TDoc, TId, TQuerySession> Build<TDoc, TId>();
}