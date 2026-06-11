using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Convenience helpers for hooking observers into an <see cref="IProjectionDaemon"/>.
/// </summary>
public static class ProjectionDaemonExtensions
{
    /// <summary>
    /// Subscribe an <see cref="IObserver{ShardState}"/> to this daemon's
    /// <see cref="IProjectionDaemon.Tracker"/> with the daemon's
    /// <see cref="IProjectionDaemon.StoreUri"/> stamped onto every state on
    /// the way through. Use this from any host integration that registers a
    /// singleton observer against multiple daemons (e.g. Wolverine's
    /// per-store <c>EventStoreAgents</c>) — without the stamper, the
    /// downstream consumer can't tell which store an upstream callback came
    /// from because <see cref="ShardStateTracker"/> is per-database and can
    /// fan in from several stores.
    ///
    /// <para>
    /// When the daemon has a null or empty <see cref="IProjectionDaemon.StoreUri"/>
    /// (test scaffolding, very old client) this falls back to a direct
    /// subscription so the observer still receives state — just unstamped,
    /// the same shape it would have seen before PS#5.
    /// </para>
    ///
    /// <para>
    /// Tracks JasperFx/ProductSupport#5.
    /// </para>
    /// </summary>
    /// <returns>The same disposable that
    /// <see cref="ShardStateTracker.Subscribe"/> returns — call
    /// <see cref="IDisposable.Dispose"/> on it to detach.</returns>
    public static IDisposable SubscribeWithStoreUriStamp(this IProjectionDaemon daemon, IObserver<ShardState> observer)
    {
        if (daemon is null) throw new ArgumentNullException(nameof(daemon));
        if (observer is null) throw new ArgumentNullException(nameof(observer));

        var storeUri = daemon.StoreUri;
        if (string.IsNullOrEmpty(storeUri))
        {
            // Nothing to stamp — fall back to direct subscription so a
            // legacy / test daemon still wires the observer through.
            return daemon.Tracker.Subscribe(observer);
        }

        return daemon.Tracker.Subscribe(new StoreUriStampingObserver(observer, storeUri));
    }
}
