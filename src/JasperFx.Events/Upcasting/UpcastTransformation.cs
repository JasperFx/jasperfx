using System.Text.Json;
using JasperFx.Core.Reflection;
using static JasperFx.Events.EventTypeExtensions;

namespace JasperFx.Events.Upcasting;

/// <summary>
/// One registered event payload transformation: for a stored event type name, how the payload
/// becomes an instance of the new CLR event type, in both the sync and async read paths.
/// </summary>
/// <remarks>
/// <para>
/// The store-agnostic promotion of Marten's
/// <c>Marten.Services.Json.Transformations.JsonTransformation</c>: the same sync/async delegate
/// pair, but over the abstract <see cref="IUpcastPayload"/> instead of
/// <c>(ISerializer, DbDataReader, index)</c>, and carrying its own <see cref="EventType"/> and
/// <see cref="EventTypeName"/> so a registry can be keyed without a separate mapping step.
/// </para>
/// <para>
/// Build instances through the static factories (<see cref="For{TOldEvent,TEvent}(Func{TOldEvent,TEvent},string?)"/>
/// and friends) or from an <see cref="IEventUpcaster"/>; the raw constructor exists for stores and
/// tests that need full control.
/// </para>
/// </remarks>
public sealed class UpcastTransformation
{
    public UpcastTransformation(
        Type eventType,
        string eventTypeName,
        Func<IUpcastPayload, object> upcast,
        Func<IUpcastPayload, CancellationToken, ValueTask<object>>? upcastAsync = null)
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        EventTypeName = eventTypeName ?? throw new ArgumentNullException(nameof(eventTypeName));
        Upcast = upcast ?? throw new ArgumentNullException(nameof(upcast));
        UpcastAsync = upcastAsync ?? ((payload, _) => new ValueTask<object>(upcast(payload)));
    }

    /// <summary>
    /// The new CLR event type this transformation produces. Events read through this
    /// transformation report this type — projections and aggregations only ever see the new type.
    /// </summary>
    public Type EventType { get; }

    /// <summary>
    /// The stored event type name this transformation claims — the SOURCE name. Once registered,
    /// this transformation is the authoritative interpretation of that name on read.
    /// </summary>
    public string EventTypeName { get; }

    /// <summary>
    /// The synchronous transformation. Async-only registrations throw
    /// <see cref="UpcastingException"/> from here.
    /// </summary>
    public Func<IUpcastPayload, object> Upcast { get; }

    /// <summary>
    /// The asynchronous transformation. Defaults to wrapping <see cref="Upcast"/> when no async
    /// variant was supplied.
    /// </summary>
    public Func<IUpcastPayload, CancellationToken, ValueTask<object>> UpcastAsync { get; }

    /// <summary>
    /// Typed sync upcast: deserialize the payload as <typeparamref name="TOldEvent"/> through the
    /// store's serializer, then map it to <typeparamref name="TEvent"/>.
    /// </summary>
    /// <param name="upcast">The old-to-new mapping function.</param>
    /// <param name="eventTypeName">
    /// The stored event type name to claim. Defaults to <typeparamref name="TOldEvent"/>'s
    /// conventional event type name, which is what makes previously stored rows of the old type
    /// deserialize through the upcast with no further mapping.
    /// </param>
    public static UpcastTransformation For<TOldEvent, TEvent>(
        Func<TOldEvent, TEvent> upcast,
        string? eventTypeName = null)
        where TOldEvent : notnull
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(upcast);

        return new UpcastTransformation(
            typeof(TEvent),
            eventTypeName ?? GetEventTypeName<TOldEvent>(),
            payload => upcast(payload.As<TOldEvent>()),
            async (payload, token) =>
                upcast(await payload.AsAsync<TOldEvent>(token).ConfigureAwait(false)));
    }

    /// <summary>
    /// Typed async-only upcast. The synchronous read path throws <see cref="UpcastingException"/>;
    /// prefer the sync form unless the transformation genuinely needs to await.
    /// </summary>
    public static UpcastTransformation For<TOldEvent, TEvent>(
        Func<TOldEvent, CancellationToken, Task<TEvent>> upcastAsync,
        string? eventTypeName = null)
        where TOldEvent : notnull
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(upcastAsync);

        return new UpcastTransformation(
            typeof(TEvent),
            eventTypeName ?? GetEventTypeName<TOldEvent>(),
            _ => throw new UpcastingException(
                $"Cannot use the upcast of event '{typeof(TOldEvent).FullNameInCode()}' to '{typeof(TEvent).FullNameInCode()}' in the synchronous API. It was registered as async only."),
            async (payload, token) =>
                await upcastAsync(await payload.AsAsync<TOldEvent>(token).ConfigureAwait(false), token)
                    .ConfigureAwait(false));
    }

    /// <summary>
    /// Raw System.Text.Json sync upcast: transform the stored payload's
    /// <see cref="JsonDocument"/> directly, with no old CLR type kept in the codebase.
    /// </summary>
    /// <param name="upcast">The JSON-to-new-type mapping function.</param>
    /// <param name="eventTypeName">
    /// The stored event type name to claim. Defaults to <typeparamref name="TEvent"/>'s
    /// conventional event type name — the "same name, older JSON schema" case.
    /// </param>
    public static UpcastTransformation FromJson<TEvent>(
        Func<JsonDocument, TEvent> upcast,
        string? eventTypeName = null)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(upcast);

        return new UpcastTransformation(
            typeof(TEvent),
            eventTypeName ?? GetEventTypeName<TEvent>(),
            payload =>
            {
                using var document = payload.AsJsonDocument();
                return upcast(document);
            },
            async (payload, token) =>
            {
                using var document = await payload.AsJsonDocumentAsync(token).ConfigureAwait(false);
                return upcast(document);
            });
    }

    /// <summary>
    /// Raw System.Text.Json async-only upcast. The synchronous read path throws
    /// <see cref="UpcastingException"/>.
    /// </summary>
    public static UpcastTransformation FromJson<TEvent>(
        Func<JsonDocument, CancellationToken, Task<TEvent>> upcastAsync,
        string? eventTypeName = null)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(upcastAsync);

        return new UpcastTransformation(
            typeof(TEvent),
            eventTypeName ?? GetEventTypeName<TEvent>(),
            _ => throw new UpcastingException(
                $"Cannot use the JSON transformation to event '{typeof(TEvent).FullNameInCode()}' in the synchronous API. It was registered as async only."),
            async (payload, token) =>
            {
                using var document = await payload.AsJsonDocumentAsync(token).ConfigureAwait(false);
                return await upcastAsync(document, token).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Wrap a class-based <see cref="IEventUpcaster"/> as a transformation.
    /// </summary>
    public static UpcastTransformation For(IEventUpcaster upcaster)
    {
        ArgumentNullException.ThrowIfNull(upcaster);

        return new UpcastTransformation(
            upcaster.EventType,
            upcaster.EventTypeName,
            upcaster.Upcast,
            upcaster.UpcastAsync);
    }
}
