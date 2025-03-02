using JasperFx.Events.Projections;

namespace JasperFx.Events.NewStuff;

public interface IAggregatorSource<TQuerySession>
{
    Type AggregateType { get; }
    IAggregator<T, TQuerySession> Build<T>();
}