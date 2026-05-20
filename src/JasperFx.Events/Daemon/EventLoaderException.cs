using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Thrown when an event store fails to load events for a projection shard.
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>EventLoaderException</c> (which extended <c>MartenException</c> and
/// was typed to <c>IMartenDatabase</c>) and re-typed to the shared
/// <see cref="IEventDatabase"/> so the lifted <see cref="ResilientEventLoader"/> can throw it.
/// Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public class EventLoaderException : Exception
{
    public EventLoaderException(ShardName name, IEventDatabase database, Exception innerException) : base(
        $"Failure while trying to load events for projection shard '{name}@{database.Identifier}'",
        innerException)
    {
    }
}
