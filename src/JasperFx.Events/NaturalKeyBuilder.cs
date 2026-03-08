using System;

namespace JasperFx.Events;

/// <summary>
/// Fluent builder for configuring natural key event mappings on a projection.
/// </summary>
public class NaturalKeyBuilder<TDoc>
{
    private readonly NaturalKeyDefinition _definition;

    internal NaturalKeyBuilder(NaturalKeyDefinition definition)
    {
        _definition = definition;
    }

    /// <summary>
    /// Register an event type that sets or changes the natural key value.
    /// </summary>
    /// <param name="extractor">Lambda to extract the natural key value from the event.</param>
    /// <typeparam name="TEvent">The event type that carries the natural key value.</typeparam>
    public NaturalKeyBuilder<TDoc> SetBy<TEvent>(Func<TEvent, object?> extractor)
    {
        _definition.EventMappings.Add(new NaturalKeyEventMapping(
            typeof(TEvent),
            e => extractor((TEvent)e)));
        return this;
    }
}
