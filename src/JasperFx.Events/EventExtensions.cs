using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Tags;

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
    /// <remarks>
    /// This overload leaves <see cref="EventRecord.Tags" /> behind, because it has no tag registrations to read them
    /// with. A store's step-through uses
    /// <see cref="WithRecordMetadata(IEvent, EventRecord, StreamIdentity, IEnumerable{ITagTypeRegistration})" />.
    /// </remarks>
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
    /// <see cref="WithRecordMetadata(IEvent, EventRecord, StreamIdentity)" />, and the record's
    /// <see cref="EventRecord.Tags" /> back onto the event as typed <see cref="EventTag" />s, so a projection grouped
    /// with <c>IdentityFromTag&lt;TTag&gt;()</c> groups an event read from a record the way the daemon groups the
    /// stored one (jasperfx#1254). This is the overload a store's step-through <c>toEvent</c> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each key is resolved with <see cref="TagTypeRegistrationExtensions.FindByTagName" />, the format documented on
    /// <see cref="EventRecord.Tags" />, and each value is parsed to the registration's
    /// <see cref="ITagTypeRegistration.SimpleType" /> in the invariant culture and wrapped back into its
    /// <see cref="ITagTypeRegistration.TagType" />.
    /// </para>
    /// <para>
    /// A key that names no registered tag type is left off: no projection on this store can group by it. A null value
    /// is no tag. A tag the event already carries is not added twice.
    /// </para>
    /// </remarks>
    /// <param name="event"></param>
    /// <param name="record"></param>
    /// <param name="streamIdentity"></param>
    /// <param name="tagTypes">The store's registered tag types, e.g. its event graph's <c>TagTypes</c>.</param>
    /// <returns>The same event, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// A value can't be read as the simple type its tag type wraps. The message names the tag.
    /// </exception>
    public static IEvent WithRecordMetadata(this IEvent @event, EventRecord record, StreamIdentity streamIdentity,
        IEnumerable<ITagTypeRegistration> tagTypes)
    {
        ArgumentNullException.ThrowIfNull(tagTypes);

        @event.WithRecordMetadata(record, streamIdentity);

        if (record.Tags is not { Count: > 0 } tags) return @event;

        var registrations = tagTypes as IReadOnlyList<ITagTypeRegistration> ?? tagTypes.ToList();
        if (registrations.Count == 0) return @event;

        foreach (var (name, text) in tags)
        {
            if (text == null) continue;
            if (registrations.FindByTagName(name) is not { } registration) continue;

            var tag = new EventTag(registration.TagType, tagFromText(record, name, text, registration));
            if (@event.Tags is { } existing && existing.Contains(tag)) continue;

            @event.AddTag(tag);
        }

        return @event;
    }

    // A record's tag text as an instance of the registered tag type
    private static object tagFromText(EventRecord record, string name, string text, ITagTypeRegistration registration)
    {
        var simpleType = registration.SimpleType;

        object simple;
        try
        {
            simple = simpleType == typeof(string) ? text
                : simpleType == typeof(Guid) ? Guid.Parse(text)
                : Convert.ChangeType(text, simpleType, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException(
                $"Event {record.EventId} has tag '{name}' = '{text}', which can't be read as the {simpleType.Name} that tag type {registration.TagType.FullNameInCode()} wraps.",
                nameof(record), e);
        }

        if (registration.TagType == simpleType) return simple;

        return StrongTypedIdResolver.TryResolve(registration.TagType, out var info, StrongTypedIdStyle.AnyType)
            ? StrongTypedIdResolver.Wrap(info, simple)
            : throw new InvalidOperationException(
                $"Tag type {registration.TagType.FullNameInCode()} wraps a {simpleType.Name}, but JasperFx can't build one from a value. A tag type read back from a record has to wrap a single value it can be constructed from.");
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