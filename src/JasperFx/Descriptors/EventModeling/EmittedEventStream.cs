using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// The stream lane of one event a slice emits: which aggregate's stream the event is appended to
/// (jasperfx#1203). See <see cref="EventModelSliceDescriptor.EmittedEventStreams"/>.
/// </summary>
/// <param name="Event">The emitted event.</param>
/// <param name="Aggregate">The aggregate whose stream the event goes to.</param>
public sealed record EmittedEventStream(TypeDescriptor Event, TypeDescriptor Aggregate);
