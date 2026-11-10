using System.Diagnostics.CodeAnalysis;
using JasperFx.Descriptors;

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
        return @event.CloneEventWithNewData(eventData);
    }

    /// <summary>
    /// Stamps a freshly wrapped event with the identity and position a wire <see cref="EventRecord"/>
    /// carries, for a store turning a record back into an event it can fold (jasperfx#1049). A stream id
    /// that is not a Guid on a Guid-identified store lands on <see cref="IEvent.StreamKey"/>, where the
    /// fold can still see it, instead of disappearing.
    /// </summary>
    /// <param name="event"></param>
    /// <param name="record"></param>
    /// <param name="streamIdentity"></param>
    /// <returns>The same event, for chaining.</returns>
    public static IEvent WithRecordMetadata(this IEvent @event, EventRecord record, StreamIdentity streamIdentity)
    {
        @event.Id = record.EventId;
        @event.Sequence = record.Sequence;
        @event.Version = record.StreamVersion;
        @event.Timestamp = record.Timestamp;
        @event.TenantId = record.TenantId ?? StorageConstants.DefaultTenantId;
        @event.EventTypeName = record.EventTypeName;
        @event.DotNetTypeName = @event.EventType.AssemblyQualifiedName ?? @event.EventType.FullName!;

        if (streamIdentity == StreamIdentity.AsGuid && Guid.TryParse(record.StreamId, out var streamId))
        {
            @event.StreamId = streamId;
        }
        else
        {
            @event.StreamKey = record.StreamId;
        }

        return @event;
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