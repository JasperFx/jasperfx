using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Decorator <see cref="IObserver{T}"/> that stamps a fixed
/// <see cref="ShardState.StoreUri"/> onto every state before forwarding to
/// an inner observer. Solves the multi-store attribution problem at the
/// <see cref="ShardStateTracker"/> layer:
///
/// <para>
/// A <see cref="ShardStateTracker"/> is shared across every
/// <see cref="IEventStore"/> attached to the same database (e.g. an
/// ancillary store added via <c>AddMartenStore&lt;T&gt;</c> that lives in
/// the same Postgres database as a sibling store). The bare
/// <see cref="ShardState"/> carries no store identity, so a singleton
/// <see cref="IObserver{ShardState}"/> registered through a single daemon's
/// <c>Tracker.Subscribe</c> can't tell which store an upstream callback came
/// from. The stamper closes over the owning store's URI at subscription
/// time — where it's known — and stamps it on the way through.
/// </para>
///
/// <para>
/// Lives at the JasperFx.Events layer so every store integration
/// (Marten, Polecat, future) uses the same shape. Consumers attach it via
/// <see cref="ProjectionDaemonExtensions.SubscribeWithStoreUriStamp"/>.
/// </para>
///
/// <para>
/// Tracks JasperFx/ProductSupport#5: live shard states + HighWaterMark
/// snapshots on multi-store CritterWatch hosts collapsed into a single
/// bucket because nothing in the daemon chain stamped the owning store URI.
/// </para>
/// </summary>
public sealed class StoreUriStampingObserver : IObserver<ShardState>
{
    private readonly IObserver<ShardState> _inner;
    private readonly string _storeUri;

    public StoreUriStampingObserver(IObserver<ShardState> inner, string storeUri)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _storeUri = storeUri ?? throw new ArgumentNullException(nameof(storeUri));
    }

    public void OnNext(ShardState value)
    {
        StampIfMissing(value, _storeUri);
        _inner.OnNext(value);
    }

    /// <summary>
    /// Mutate <paramref name="state"/>.<see cref="ShardState.StoreUri"/> to
    /// <paramref name="storeUri"/> if-and-only-if the existing value is null
    /// or empty AND <paramref name="storeUri"/> is non-empty. Shared between
    /// this observer (the SubscribeWithStoreUriStamp extension's per-store
    /// wrapping path) and the daemon's own OnNext path
    /// (<c>JasperFxAsyncDaemon</c>, for direct
    /// <c>daemon.Tracker.Subscribe</c> consumers that bypass the extension)
    /// so both stamping locations follow the same preserve-upstream
    /// semantics — neither clobbers a value an outer decorator has already
    /// written.
    /// </summary>
    public static void StampIfMissing(ShardState state, string? storeUri)
    {
        if (string.IsNullOrEmpty(storeUri)) return;
        if (!string.IsNullOrEmpty(state.StoreUri)) return;
        state.StoreUri = storeUri;
    }

    public void OnCompleted() => _inner.OnCompleted();
    public void OnError(Exception error) => _inner.OnError(error);
}
