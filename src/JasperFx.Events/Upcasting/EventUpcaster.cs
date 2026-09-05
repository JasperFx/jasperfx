using JasperFx.Core.Reflection;
using static JasperFx.Events.EventTypeExtensions;

namespace JasperFx.Events.Upcasting;

/// <summary>
/// The class-based event payload transformation contract. Upcasting transforms an old stored event
/// schema into the new one on read: for a specific stored event type name, an upcaster produces
/// the new CLR event type, so aggregations and projections only ever see the new type.
/// </summary>
/// <remarks>
/// <para>
/// The store-agnostic promotion of Marten's
/// <c>Marten.Services.Json.Transformations.IEventUpcaster</c>. The shape is deliberately kept as
/// close to Marten's as portability allows — same members, same sync/async split — with the
/// store-specific <c>(ISerializer, DbDataReader, index)</c> triple replaced by
/// <see cref="IUpcastPayload"/>.
/// </para>
/// <para>
/// Prefer deriving from the built-in bases (<see cref="EventUpcaster{TOldEvent,TEvent}"/>,
/// <see cref="AsyncOnlyEventUpcaster{TOldEvent,TEvent}"/>, or the raw-JSON variants in
/// <c>JasperFx.Events.Upcasting.SystemTextJson</c>) over implementing this interface directly.
/// </para>
/// </remarks>
public interface IEventUpcaster
{
    /// <summary>
    /// The stored event type name this upcaster claims — the SOURCE name of the transformation.
    /// </summary>
    string EventTypeName { get; }

    /// <summary>
    /// The new CLR event type this upcaster maps to.
    /// </summary>
    Type EventType { get; }

    /// <summary>
    /// Transform the stored payload in the synchronous read path.
    /// </summary>
    object Upcast(IUpcastPayload payload);

    /// <summary>
    /// Transform the stored payload in the asynchronous read path.
    /// </summary>
    ValueTask<object> UpcastAsync(IUpcastPayload payload, CancellationToken token);
}

/// <summary>
/// Base implementation of <see cref="IEventUpcaster"/>. Implement at least the synchronous
/// <see cref="Upcast"/>; <see cref="UpcastAsync"/> delegates to it unless overridden.
/// </summary>
public abstract class EventUpcaster : IEventUpcaster
{
    public abstract Type EventType { get; }

    /// <summary>
    /// The stored event type name to transform. Defaults to the conventional event type name of
    /// <see cref="EventType"/>.
    /// </summary>
    public virtual string EventTypeName => EventType.GetEventTypeName();

    public abstract object Upcast(IUpcastPayload payload);

    public virtual ValueTask<object> UpcastAsync(IUpcastPayload payload, CancellationToken token)
    {
        return new ValueTask<object>(Upcast(payload));
    }
}

/// <summary>
/// Base implementation of <see cref="EventUpcaster"/> for a known new CLR event type.
/// </summary>
/// <typeparam name="TEvent">The new CLR event type.</typeparam>
public abstract class EventUpcaster<TEvent> : EventUpcaster where TEvent : notnull
{
    public override Type EventType => typeof(TEvent);
}

/// <summary>
/// Typed upcaster: deserializes the stored payload as <typeparamref name="TOldEvent"/> through
/// the store's serializer and maps it to <typeparamref name="TEvent"/> via <see cref="Upcast(TOldEvent)"/>.
/// The stored event type name defaults to <typeparamref name="TOldEvent"/>'s conventional name,
/// which is what makes previously stored rows of the old type flow through the upcast.
/// </summary>
/// <typeparam name="TOldEvent">The old CLR event type.</typeparam>
/// <typeparam name="TEvent">The new CLR event type.</typeparam>
public abstract class EventUpcaster<TOldEvent, TEvent> : EventUpcaster<TEvent>
    where TOldEvent : notnull
    where TEvent : notnull
{
    public override string EventTypeName => GetEventTypeName<TOldEvent>();

    public override object Upcast(IUpcastPayload payload)
    {
        return Upcast(payload.As<TOldEvent>());
    }

    public override async ValueTask<object> UpcastAsync(IUpcastPayload payload, CancellationToken token)
    {
        return Upcast(await payload.AsAsync<TOldEvent>(token).ConfigureAwait(false));
    }

    /// <summary>
    /// Map the deserialized old event to the new event type.
    /// </summary>
    protected abstract TEvent Upcast(TOldEvent oldEvent);
}

/// <summary>
/// Async-only typed upcaster, for transformations that genuinely need to await. Only usable in a
/// store's asynchronous read path; the synchronous path throws <see cref="UpcastingException"/>.
/// Prefer <see cref="EventUpcaster{TOldEvent,TEvent}"/> — an async transformation runs per stored
/// event and invites N+1 behavior.
/// </summary>
/// <typeparam name="TOldEvent">The old CLR event type.</typeparam>
/// <typeparam name="TEvent">The new CLR event type.</typeparam>
public abstract class AsyncOnlyEventUpcaster<TOldEvent, TEvent> : EventUpcaster<TEvent>
    where TOldEvent : notnull
    where TEvent : notnull
{
    public override string EventTypeName => GetEventTypeName<TOldEvent>();

    public override object Upcast(IUpcastPayload payload)
    {
        throw new UpcastingException(
            $"Cannot use AsyncOnlyEventUpcaster of type {GetType().FullNameInCode()} in the synchronous API.");
    }

    public override async ValueTask<object> UpcastAsync(IUpcastPayload payload, CancellationToken token)
    {
        return await UpcastAsync(await payload.AsAsync<TOldEvent>(token).ConfigureAwait(false), token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Map the deserialized old event to the new event type, asynchronously.
    /// </summary>
    protected abstract Task<TEvent> UpcastAsync(TOldEvent oldEvent, CancellationToken token);
}
