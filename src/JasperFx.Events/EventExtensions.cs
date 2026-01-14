using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events;

public static class EventExtensions
{
    /// <summary>
    /// Clones the metadata from one event wrapper to another with replaced data.
    /// This is useful for "event enrichment" during projections
    /// </summary>
    /// <param name="event"></param>
    /// <param name="eventData"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEvent<T> WithData<T>(this IEvent @event, T eventData) where T : notnull
    {
        return new Event<T>(eventData)
        {
            Id = @event.Id,
            Sequence = @event.Sequence,
            TenantId = @event.TenantId,
            Version = @event.Version,
            StreamId = @event.StreamId,
            StreamKey = @event.StreamKey,
            Timestamp = @event.Timestamp,
            Headers = @event.Headers,
        };
    }

    /// <summary>
    /// Tries to find a referenced entity of type T in this EventSlice. Finds the first in the events order
    /// if more than one is found
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static bool TryFindReference<T>(this IEnumerable<IEvent> events, [NotNullWhen(true)] out T? entity) where T : class
    {
        var match = events.OfType<IEvent<References<T>>>().FirstOrDefault();
        if (match != null)
        {
            entity = match.Data.Entity;
            return true;
        }

        entity = null;
        return false;
    }
    
    /// <summary>
    /// Retrieve all referenced entities of type T in this event slice by looking for
    /// References<T> events 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEnumerable<T> AllReferenced<T>(this IEnumerable<IEvent> events)
    {
        return events.OfType<IEvent<References<T>>>().Select(x => x.Data.Entity);
    }
}