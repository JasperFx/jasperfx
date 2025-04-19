using JasperFx.Core.Reflection;

namespace JasperFx.Events.Aggregation;

public interface IAggregator<T, TSession>
{
    ValueTask<T?> BuildAsync(
        IReadOnlyList<IEvent> events,
        TSession session,
        T? snapshot,
        CancellationToken cancellation);
    
    Type IdentityType { get; }
}

public interface IAggregator<T, TId, TSession> : IAggregator<T, TSession>
{
    ValueTask<T?> BuildAsync(IReadOnlyList<IEvent> events,
        TSession session,
        T? snapshot,
        TId id,
        IIdentitySetter<T, TId> identitySetter,
        CancellationToken cancellation);
}



