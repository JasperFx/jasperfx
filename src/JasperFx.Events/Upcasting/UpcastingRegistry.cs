using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ImTools;

namespace JasperFx.Events.Upcasting;

/// <summary>
/// The shared registration and resolution surface for event upcasting, exposed by every store as
/// <see cref="EventRegistry.Upcasters"/>. Registration happens at configuration time through the
/// fluent <c>Upcast</c> overloads (kept deliberately close to Marten's
/// <c>IEventStoreOptions.Upcast</c> family); at read time a store resolves a stored event type
/// name through <see cref="TryFindTransformation"/> inside its own hydration path.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a store owes this registry on its read path.</b> Wherever the store turns a stored row
/// into an <see cref="IEvent"/> — stream reads, aggregation, <c>FetchForWriting</c>, the async
/// daemon, subscriptions; every path that deserializes an event — it must first consult
/// <see cref="TryFindTransformation"/> with the stored event type name. On a hit, the payload is
/// produced by the transformation (over the store's <see cref="IUpcastPayload"/> adapter) instead
/// of default deserialization, and the resulting envelope reports the transformation's
/// <see cref="UpcastTransformation.EventType"/>.
/// </para>
/// <para>
/// <b>The authority rule (marten#4680).</b> A registered transformation is the authoritative
/// interpretation of its source event type name. Stores that persist a stored-CLR-type hint
/// alongside the event type name (Marten's <c>mt_dotnet_type</c>) must NOT let that hint override
/// a registered upcast: when the old CLR type is appended typed into the same store, its rows
/// still read back through the upcaster. Any alternate-mapping swap keyed on the stored CLR type
/// name applies only to names with no registered transformation.
/// </para>
/// <para>
/// Registration is last-wins per event type name, matching Marten: re-registering a name replaces
/// the earlier transformation.
/// </para>
/// </remarks>
public class UpcastingRegistry
{
    private ImHashMap<string, UpcastTransformation> _byName = ImHashMap<string, UpcastTransformation>.Empty;
    private readonly List<UpcastTransformation> _all = new();

    /// <summary>
    /// True if at least one transformation has been registered. Lets a store keep its unmodified
    /// hydration path when upcasting is unused.
    /// </summary>
    public bool HasAny => _all.Count != 0;

    /// <summary>
    /// Every registered transformation, in registration order (including ones later replaced by a
    /// re-registration of the same name). Stores use this to pre-register the target event types.
    /// </summary>
    public IReadOnlyList<UpcastTransformation> AllTransformations => _all;

    /// <summary>
    /// Resolve the transformation registered for a stored event type name, if any. This is the
    /// call a store makes on its read path before default deserialization.
    /// </summary>
    public bool TryFindTransformation(string eventTypeName,
        [NotNullWhen(true)] out UpcastTransformation? transformation)
    {
        return _byName.TryFind(eventTypeName, out transformation);
    }

    /// <summary>
    /// True if the given stored event type name has a registered transformation — i.e. the name is
    /// an upcast SOURCE whose interpretation is authoritative on read (marten#4680).
    /// </summary>
    public bool IsUpcastSource(string eventTypeName) => _byName.TryFind(eventTypeName, out _);

    /// <summary>
    /// The low-level registration every fluent overload funnels into.
    /// </summary>
    public UpcastingRegistry Register(UpcastTransformation transformation)
    {
        ArgumentNullException.ThrowIfNull(transformation);

        _all.Add(transformation);
        _byName = _byName.AddOrUpdate(transformation.EventTypeName, transformation);
        return this;
    }

    /// <summary>
    /// Typed upcast from the old CLR event type to the new one. The stored event type name is
    /// <typeparamref name="TOldEvent"/>'s conventional name.
    /// </summary>
    public UpcastingRegistry Upcast<TOldEvent, TEvent>(Func<TOldEvent, TEvent> upcast)
        where TOldEvent : notnull
        where TEvent : notnull
    {
        return Register(UpcastTransformation.For(upcast));
    }

    /// <summary>
    /// Typed upcast from the old CLR event type to the new one, claiming an explicit stored event
    /// type name.
    /// </summary>
    public UpcastingRegistry Upcast<TOldEvent, TEvent>(string eventTypeName, Func<TOldEvent, TEvent> upcast)
        where TOldEvent : notnull
        where TEvent : notnull
    {
        return Register(UpcastTransformation.For(upcast, eventTypeName));
    }

    /// <summary>
    /// Async-only typed upcast. Only usable in a store's asynchronous read path; the synchronous
    /// path throws <see cref="UpcastingException"/>.
    /// </summary>
    public UpcastingRegistry Upcast<TOldEvent, TEvent>(Func<TOldEvent, CancellationToken, Task<TEvent>> upcastAsync)
        where TOldEvent : notnull
        where TEvent : notnull
    {
        return Register(UpcastTransformation.For(upcastAsync));
    }

    /// <summary>
    /// Async-only typed upcast claiming an explicit stored event type name.
    /// </summary>
    public UpcastingRegistry Upcast<TOldEvent, TEvent>(string eventTypeName,
        Func<TOldEvent, CancellationToken, Task<TEvent>> upcastAsync)
        where TOldEvent : notnull
        where TEvent : notnull
    {
        return Register(UpcastTransformation.For(upcastAsync, eventTypeName));
    }

    /// <summary>
    /// Raw System.Text.Json upcast for the stored event type name matching
    /// <typeparamref name="TEvent"/>'s conventional name — the "same name, older JSON schema" case.
    /// </summary>
    public UpcastingRegistry Upcast<TEvent>(Func<JsonDocument, TEvent> upcast)
        where TEvent : notnull
    {
        return Register(UpcastTransformation.FromJson(upcast));
    }

    /// <summary>
    /// Raw System.Text.Json upcast claiming an explicit stored event type name.
    /// </summary>
    public UpcastingRegistry Upcast<TEvent>(string eventTypeName, Func<JsonDocument, TEvent> upcast)
        where TEvent : notnull
    {
        return Register(UpcastTransformation.FromJson(upcast, eventTypeName));
    }

    /// <summary>
    /// Async-only raw System.Text.Json upcast. Only usable in a store's asynchronous read path.
    /// </summary>
    public UpcastingRegistry AsyncOnlyUpcast<TEvent>(Func<JsonDocument, CancellationToken, Task<TEvent>> upcastAsync)
        where TEvent : notnull
    {
        return Register(UpcastTransformation.FromJson(upcastAsync));
    }

    /// <summary>
    /// Async-only raw System.Text.Json upcast claiming an explicit stored event type name.
    /// </summary>
    public UpcastingRegistry AsyncOnlyUpcast<TEvent>(string eventTypeName,
        Func<JsonDocument, CancellationToken, Task<TEvent>> upcastAsync)
        where TEvent : notnull
    {
        return Register(UpcastTransformation.FromJson(upcastAsync, eventTypeName));
    }

    /// <summary>
    /// Register one or more class-based upcasters.
    /// </summary>
    public UpcastingRegistry Upcast(params IEventUpcaster[] upcasters)
    {
        foreach (var upcaster in upcasters)
        {
            Register(UpcastTransformation.For(upcaster));
        }

        return this;
    }

    /// <summary>
    /// Register a class-based upcaster by type.
    /// </summary>
    public UpcastingRegistry Upcast<TUpcaster>() where TUpcaster : IEventUpcaster, new()
    {
        return Register(UpcastTransformation.For(new TUpcaster()));
    }
}
