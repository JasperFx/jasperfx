using System.Diagnostics;
using JasperFx.Events.Projections;
using Polly;

namespace JasperFx.Events.Daemon;

/// <summary>
/// An <see cref="IEventLoader"/> decorator that runs the inner loader through a
/// <see cref="ResiliencePipeline"/> and tracks loading metrics via
/// <c>EventRequest.Metrics.TrackLoading()</c>. Failures are wrapped in an
/// <see cref="EventLoaderException"/> carrying the shard name + database identifier.
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>ResilientEventLoader</c>. The only store-specific tail was the
/// <see cref="EventLoaderException"/> wrap typed to <c>IMartenDatabase</c>; the database is now
/// supplied as an <see cref="IEventDatabase"/> ctor arg so the decorator wraps any
/// <see cref="IEventLoader"/> without an interface change. Adopting this shared decorator also
/// gives Polecat loading-metrics parity for free (it previously inlined the Polly call with no
/// <c>TrackLoading</c>). Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public class ResilientEventLoader : IEventLoader
{
    private readonly ResiliencePipeline _pipeline;
    private readonly IEventLoader _inner;
    private readonly IEventDatabase _database;

    private record EventLoadExecution(EventRequest Request, IEventLoader Loader)
    {
        public async ValueTask<EventPage> ExecuteAsync(CancellationToken token)
        {
            using Activity? activity = Request.Metrics.TrackLoading(Request);

            try
            {
                var results = await Loader.LoadAsync(Request, token).ConfigureAwait(false);
                return results;
            }
            catch (Exception e)
            {
                activity?.AddException(e);
                throw;
            }
            finally
            {
                activity?.Stop();
            }
        }
    }

    public ResilientEventLoader(ResiliencePipeline pipeline, IEventLoader inner, IEventDatabase database)
    {
        _pipeline = pipeline;
        _inner = inner;
        _database = database;
    }

    public Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
    {
        try
        {
            var execution = new EventLoadExecution(request, _inner);
            return _pipeline.ExecuteAsync(static (x, t) => x.ExecuteAsync(t), execution, token).AsTask();
        }
        catch (Exception e)
        {
            throw new EventLoaderException(request.Name, _database, e);
        }
    }
}
