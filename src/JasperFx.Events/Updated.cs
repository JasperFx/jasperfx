using JasperFx.Events.Daemon;

namespace JasperFx.Events;

/// <summary>
/// Stand in event used by projections to just denote a document or entity
/// that was updated as part of an input to an aggregate projection. Synthetic event.
/// </summary>
/// <param name="Document"></param>
/// <typeparam name="T"></typeparam>
public record Updated<T>(string TenantId, T Entity, ActionType Action) : References<T>(Entity), IUpdatedEntity
{
    public IEvent ToEvent()
    {
        return new Event<Updated<T>>(this)
        {
            TenantId = TenantId
        };
    }

    object IUpdatedEntity.Entity => Entity;
}

public interface ICanWrapEvent
{
    IEvent ToEvent();
}

public interface IUpdatedEntity : ICanWrapEvent
{
    IEvent ToEvent();
    object Entity { get; }
}