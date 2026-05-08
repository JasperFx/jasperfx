namespace JasperFx.Events.EventModeling;

/// <summary>
/// Base class application authors derive from to declare an Event
/// Modeling topology — the swim-lane / slice description that
/// CritterWatch and other operator tooling renders. Override
/// <see cref="Configure"/> to populate the supplied
/// <see cref="EventModelBuilder"/> with slices and their members.
/// </summary>
public abstract class EventModelDefinition
{
    /// <summary>
    /// Populate <paramref name="builder"/> with the slices that make up
    /// this event-model definition. Called once by the discovery layer
    /// when assembling the topology.
    /// </summary>
    /// <param name="builder">Builder that accumulates slices into a definition.</param>
    public abstract void Configure(EventModelBuilder builder);
}
