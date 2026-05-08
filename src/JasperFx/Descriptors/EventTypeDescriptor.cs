namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic descriptor for a single event-type registration. Mirrors
/// the configured event-type entry on the event store so monitoring tools
/// can show which event aliases are wired up and what the underlying CLR
/// type is.
/// </summary>
/// <param name="EventType">Type identity of the CLR event class.</param>
/// <param name="Alias">Event-type alias as registered with the store.</param>
/// <param name="Description">Operator-facing description of the event; <see langword="null"/> when no description was registered.</param>
public sealed record EventTypeDescriptor(
    TypeDescriptor EventType,
    string Alias,
    string? Description);
