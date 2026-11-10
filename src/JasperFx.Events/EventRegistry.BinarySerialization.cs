using System.Collections.Concurrent;

namespace JasperFx.Events;

public partial class EventRegistry
{
    private readonly ConcurrentDictionary<Type, IEventBinarySerializer> _binarySerializerByType = new();

    // What each type resolved to, because the appended-event path asks once per event and the attribute
    // probe is the expensive half. Cleared by anything that could change an answer.
    private readonly ConcurrentDictionary<Type, BinaryResolution> _binaryResolutions = new();

    private IEventBinarySerializer? _defaultBinarySerializer;

    private readonly record struct BinaryResolution(IEventBinarySerializer? Serializer, bool IsMarked);

    /// <summary>
    ///     The store-wide fallback for event types marked <see cref="BinaryEventAttribute" /> that have no
    ///     explicit <see cref="UseBinarySerializer{TEvent}" /> registration. Null by default, which leaves
    ///     every event type on the JSON path.
    /// </summary>
    /// <remarks>
    ///     May be assigned before or after the event types are registered, in either order.
    /// </remarks>
    public IEventBinarySerializer? DefaultBinarySerializer
    {
        get => _defaultBinarySerializer;
        set
        {
            _defaultBinarySerializer = value;
            _binaryResolutions.Clear();
            OnBinarySerializersChanged();
        }
    }

    /// <summary>
    ///     Store <typeparamref name="TEvent" /> through <paramref name="serializer" /> rather than as JSON.
    ///     Beats <see cref="BinaryEventAttribute" /> plus <see cref="DefaultBinarySerializer" />.
    /// </summary>
    public EventRegistry UseBinarySerializer<TEvent>(IEventBinarySerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        _binarySerializerByType[typeof(TEvent)] = serializer;
        _binaryResolutions.Clear();

        AddEventType(typeof(TEvent));
        OnBinarySerializersChanged();

        return this;
    }

    /// <summary>
    ///     A serializer registration changed, so an answer a store copied onto an event mapping may be
    ///     stale. Registration order is not fixed: a mapping built by <c>AddEventType</c> or a projection
    ///     can predate either way of configuring a serializer.
    /// </summary>
    protected virtual void OnBinarySerializersChanged()
    {
    }

    /// <summary>
    ///     The serializer governing an event type, or null when none is configured. Explicit registration
    ///     beats <see cref="BinaryEventAttribute" /> plus <see cref="DefaultBinarySerializer" />.
    /// </summary>
    /// <remarks>
    ///     Null for a marked type with nothing configured, which is the answer for building an event
    ///     mapping: mappings are built on read paths and before a serializer is assigned. A store deciding
    ///     how to write uses <see cref="ResolveBinarySerializerFor" />.
    /// </remarks>
    public IEventBinarySerializer? FindBinarySerializerFor(Type eventType)
    {
        return binaryResolutionFor(eventType).Serializer;
    }

    /// <summary>
    ///     As <see cref="FindBinarySerializerFor" />, but refuses a type that asks for binary serialization
    ///     and has no serializer to use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The type carries <see cref="BinaryEventAttribute" /> with no serializer configured. Deliberately
    ///     not a fall back to JSON, which would write rows in a format the operator did not choose.
    /// </exception>
    public IEventBinarySerializer? ResolveBinarySerializerFor(Type eventType)
    {
        var resolution = binaryResolutionFor(eventType);
        if (resolution is { Serializer: null, IsMarked: true })
        {
            throw new InvalidOperationException(
                $"Event type '{eventType.FullName}' is marked with [BinaryEvent] but no IEventBinarySerializer was registered. " +
                $"Either call opts.Events.UseBinarySerializer<{eventType.Name}>(...) explicitly, " +
                "or set opts.Events.DefaultBinarySerializer to a store-wide fallback.");
        }

        return resolution.Serializer;
    }

    /// <summary>
    ///     The binary body of an event on the write path, or null when its type stays on the JSON path.
    /// </summary>
    /// <exception cref="InvalidOperationException">See <see cref="ResolveBinarySerializerFor" />.</exception>
    public byte[]? SerializeBinaryEventData(IEvent @event)
    {
        var eventType = @event.EventType;
        return ResolveBinarySerializerFor(eventType)?.Serialize(eventType, @event.Data);
    }

    private BinaryResolution binaryResolutionFor(Type eventType)
    {
        return _binaryResolutions.GetOrAdd(eventType, static (type, registry) =>
        {
            // inherit: false governs the EVENT type's hierarchy, and matches BinaryEventAttribute's own
            // Inherited = false. IsDefined still finds an attribute derived from this one, Marten's.
            var isMarked = type.IsDefined(typeof(BinaryEventAttribute), inherit: false);

            var serializer = registry._binarySerializerByType.TryGetValue(type, out var explicitSerializer)
                ? explicitSerializer
                : isMarked
                    ? registry._defaultBinarySerializer
                    : null;

            return new BinaryResolution(serializer, isMarked);
        }, this);
    }
}
