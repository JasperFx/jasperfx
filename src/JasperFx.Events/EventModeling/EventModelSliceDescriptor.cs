using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Diagnostic descriptor for a single slice in an Event Modeling
/// definition. Snapshot of what an <see cref="EventModelSliceBuilder"/>
/// captured, ready for transport over the wire to operator tooling.
/// </summary>
/// <param name="Name">Display name of the slice.</param>
/// <param name="TriggerLabel">Free-form trigger label, when one was supplied.</param>
/// <param name="TriggerType">CLR trigger type, when one was supplied.</param>
/// <param name="CommandType">CLR command type dispatched by the slice, when one was declared.</param>
/// <param name="HandlerType">CLR handler / aggregate type for the command, when one was declared.</param>
/// <param name="EmittedEvents">CLR event types emitted by the slice, in declaration order.</param>
/// <param name="ProjectionTypes">CLR projection types that consume the slice's events.</param>
/// <param name="ReadModelTypes">CLR read-model types the slice reads from.</param>
public sealed record EventModelSliceDescriptor(
    string Name,
    string? TriggerLabel,
    TypeDescriptor? TriggerType,
    TypeDescriptor? CommandType,
    TypeDescriptor? HandlerType,
    IReadOnlyList<TypeDescriptor> EmittedEvents,
    IReadOnlyList<TypeDescriptor> ProjectionTypes,
    IReadOnlyList<TypeDescriptor> ReadModelTypes);

/// <summary>
/// Diagnostic descriptor for an entire Event Modeling definition.
/// Carries the slice list produced by an
/// <see cref="EventModelDefinition"/> after configuration runs.
/// </summary>
/// <param name="Name">Display name of the model.</param>
/// <param name="Slices">Slices that make up the model, in declaration order.</param>
public sealed record EventModelDescriptor(
    string Name,
    IReadOnlyList<EventModelSliceDescriptor> Slices);
