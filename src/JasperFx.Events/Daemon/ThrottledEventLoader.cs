using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#494 (epic #486 WS2): bounds how many subscription agents load events concurrently
/// against one database. Each IEventLoader.LoadAsync opens its own store session, so N running
/// agents drive the connection pool's high-water mark toward N — under per-tenant event
/// partitioning that is (projections × tenants) — even though measurement shows only a handful
/// of loads are ever active at an instant. All of a daemon's agent loaders share one semaphore
/// sized by <see cref="DaemonSettings.MaxConcurrentEventLoadsPerDatabase"/>, collapsing the
/// steady-state connection footprint to O(databases) with no measured throughput cost.
/// </summary>
internal class ThrottledEventLoader: IEventLoader
{
    private readonly SemaphoreSlim _throttle;

    public ThrottledEventLoader(IEventLoader inner, SemaphoreSlim throttle)
    {
        Inner = inner;
        _throttle = throttle;
    }

    // Exposed for wiring assertions in tests
    internal IEventLoader Inner { get; }

    public async Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
    {
        await _throttle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Inner.LoadAsync(request, token).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();
        }
    }
}
