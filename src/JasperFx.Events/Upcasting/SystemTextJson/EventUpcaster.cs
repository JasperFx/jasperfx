using System.Text.Json;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Upcasting.SystemTextJson;

/// <summary>
/// Raw System.Text.Json upcaster: transforms the stored payload's <see cref="JsonDocument"/>
/// directly into <typeparamref name="TEvent"/>, with no old CLR type kept in the codebase. The
/// stored event type name defaults to <typeparamref name="TEvent"/>'s conventional name — the
/// "same name, older JSON schema" case; override <see cref="EventUpcaster.EventTypeName"/> for a
/// rename.
/// </summary>
/// <remarks>
/// The namespace and type names deliberately mirror Marten's
/// <c>Marten.Services.Json.Transformations.SystemTextJson</c>, so migrating an existing Marten
/// upcaster is a using-directive change. Requires a store whose serializer is
/// System.Text.Json-based — always true for Polecat and Fisher; Marten throws
/// <see cref="UpcastingException"/> from <see cref="IUpcastPayload.AsJsonDocument"/> under a
/// non-STJ serializer.
/// </remarks>
/// <typeparam name="TEvent">The new CLR event type.</typeparam>
public abstract class EventUpcaster<TEvent> : Upcasting.EventUpcaster<TEvent>
    where TEvent : notnull
{
    public override object Upcast(IUpcastPayload payload)
    {
        using var document = payload.AsJsonDocument();
        return Upcast(document);
    }

    public override async ValueTask<object> UpcastAsync(IUpcastPayload payload, CancellationToken token)
    {
        using var document = await payload.AsJsonDocumentAsync(token).ConfigureAwait(false);
        return Upcast(document);
    }

    /// <summary>
    /// Map the stored JSON to the new event type. The document is disposed by the caller.
    /// </summary>
    protected abstract TEvent Upcast(JsonDocument oldEvent);
}

/// <summary>
/// Async-only raw System.Text.Json upcaster. Only usable in a store's asynchronous read path; the
/// synchronous path throws <see cref="UpcastingException"/>. Prefer
/// <see cref="EventUpcaster{TEvent}"/> — an async transformation runs per stored event and
/// invites N+1 behavior.
/// </summary>
/// <typeparam name="TEvent">The new CLR event type.</typeparam>
public abstract class AsyncOnlyEventUpcaster<TEvent> : Upcasting.EventUpcaster<TEvent>
    where TEvent : notnull
{
    public override object Upcast(IUpcastPayload payload)
    {
        throw new UpcastingException(
            $"Cannot use AsyncOnlyEventUpcaster of type {GetType().FullNameInCode()} in the synchronous API.");
    }

    public override async ValueTask<object> UpcastAsync(IUpcastPayload payload, CancellationToken token)
    {
        using var document = await payload.AsJsonDocumentAsync(token).ConfigureAwait(false);
        return await UpcastAsync(document, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Map the stored JSON to the new event type, asynchronously. The document is disposed by the
    /// caller.
    /// </summary>
    protected abstract Task<TEvent> UpcastAsync(JsonDocument oldEvent, CancellationToken token);
}
