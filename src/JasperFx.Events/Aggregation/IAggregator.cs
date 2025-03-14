namespace JasperFx.Events.Aggregation;

public interface IAggregator<T, TSession>
{
    ValueTask<T> BuildAsync(
        IReadOnlyList<IEvent> events,
        TSession session,
        T? snapshot,
        CancellationToken cancellation);
}

internal class AsyncEvolveAggregator<T, TSession> : IAggregator<T, TSession>
{
    private readonly Func<IEvent, TSession, T, CancellationToken, ValueTask<T?>> _func;

    public AsyncEvolveAggregator(Func<IEvent, TSession, T, CancellationToken, ValueTask<T?>> func)
    {
        _func = func;
    }

    public async ValueTask<T> BuildAsync(IReadOnlyList<IEvent> events, TSession session, T? snapshot, CancellationToken cancellation)
    {
        foreach (var e in events)
        {
            snapshot = await _func(e, session, snapshot, cancellation);
        }

        return snapshot;
    }
}

internal class SyncEvolveAggregator<T, TSession> : IAggregator<T, TSession>
{
    private readonly Func<T?, IEvent, T?> _func;

    public SyncEvolveAggregator(Func<T?, IEvent, T?> func)
    {
        _func = func;
    }

    public ValueTask<T> BuildAsync(IReadOnlyList<IEvent> events, TSession session, T? snapshot, CancellationToken cancellation)
    {
        foreach (var e in events)
        {
            snapshot = _func(snapshot, e);
        }

        return new ValueTask<T>(snapshot);
    }
}
