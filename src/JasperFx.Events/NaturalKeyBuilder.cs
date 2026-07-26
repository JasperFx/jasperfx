using System;

namespace JasperFx.Events;

/// <summary>
/// Fluent builder for configuring natural key event mappings on a projection. Reach it from
/// <c>NaturalKeyFor()</c> on a single or multi stream projection. This is the supported way to bypass
/// <c>[NaturalKeySource]</c> attribute discovery when the key cannot be derived from the event by
/// convention — or when you would simply rather be explicit. See jasperfx#569.
/// </summary>
public class NaturalKeyBuilder<TDoc>
{
    private readonly NaturalKeyDefinition _definition;

    public NaturalKeyBuilder(NaturalKeyDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>
    /// Register an event type that sets or changes the natural key value. Replaces any mapping already
    /// registered for the same event type, so an explicit registration always wins over attribute
    /// discovery.
    /// </summary>
    /// <param name="extractor">Lambda to extract the natural key value from the event body.</param>
    /// <typeparam name="TEvent">The event type that carries the natural key value.</typeparam>
    public NaturalKeyBuilder<TDoc> SetBy<TEvent>(Func<TEvent, object?> extractor)
    {
        if (extractor == null) throw new ArgumentNullException(nameof(extractor));

        _definition.AddOrReplaceMapping(typeof(TEvent), e => extractor((TEvent)e.Data));
        return this;
    }

    /// <summary>
    /// <see cref="SetBy{TEvent}(System.Func{TEvent,object?})" /> for a key that also depends on event
    /// metadata — stream key, timestamp, headers — rather than the event body alone.
    /// </summary>
    /// <param name="extractor">Lambda to extract the natural key value from the event.</param>
    /// <typeparam name="TEvent">The event type that carries the natural key value.</typeparam>
    public NaturalKeyBuilder<TDoc> SetByEvent<TEvent>(Func<IEvent<TEvent>, object?> extractor) where TEvent : notnull
    {
        if (extractor == null) throw new ArgumentNullException(nameof(extractor));

        _definition.AddOrReplaceMapping(typeof(TEvent), e => extractor((IEvent<TEvent>)e));
        return this;
    }
}
