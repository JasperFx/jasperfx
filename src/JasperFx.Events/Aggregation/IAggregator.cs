namespace JasperFx.Events.Aggregation;

public interface IAggregator<T, TSession>
{
    ValueTask<T> BuildAsync(
        IReadOnlyList<IEvent> events,
        TSession session,
        T? snapshot,
        CancellationToken cancellation);
}
