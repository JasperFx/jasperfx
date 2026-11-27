using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// A consumed event that comes from outside the model — an external system's event translated in
/// (jasperfx#1298). See <see cref="EventModelSliceDescriptor.ExternalEvents"/>.
/// </summary>
/// <remarks>
/// A flag on a <see cref="EventModelSliceDescriptor.ConsumedEvents"/> entry tying it to one of the slice's
/// <see cref="EventModelSliceDescriptor.ExternalSystems"/>. Declared with <c>From&lt;T&gt;().External("Payments")</c>;
/// the derived rung sets it when the consumed type arrives on a non-local endpoint and no slice in the model
/// emits it. A canvas draws the event on the stream row with the external system's name instead of an aggregate.
/// </remarks>
/// <param name="Event">The consumed event.</param>
/// <param name="ExternalSystem">The name of the external system the event comes from.</param>
public sealed record ExternalEventDescriptor(TypeDescriptor Event, string ExternalSystem);
