namespace JasperFx.Events.EventModeling;

/// <summary>
/// Base class application authors derive from to lay an <em>overlay</em> on the
/// Event Model — names for slices, domain grouping, trigger labels, links to
/// specifications and prose hotspots for the questions still open. Override
/// <see cref="Configure"/> to populate the supplied <see cref="EventModelBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declare only what code cannot express</b> (jasperfx#687 reshape, 2026-08-20). Roles —
/// command, handler, aggregates, emitted events, published messages, projections, read models,
/// trigger kind, slice pattern — are <em>derived</em> by the sources that can see them:
/// Wolverine from its handler / HTTP / gRPC chains, the Bobcat generator from Gherkin with
/// shipped grammars. The overlay never declares them; it is folded into the model by
/// <see cref="EventModelDescriptor.Merge"/>, which sits it on the
/// <see cref="EventModelProvenance.Declared"/> rung — the bottom of the ladder — so it cannot
/// overwrite a role derived from code or observed in production, whatever order the sources were
/// registered in (jasperfx#703). The one exception is
/// <see cref="EventModelSliceBuilder.ForFlowNotOwnedHere"/> — an explicit escape hatch for flows
/// whose code this host does not own.
/// </para>
/// <para>
/// Register a definition with <c>services.AddEventModel&lt;TDefinition&gt;()</c> (or an inline
/// lambda via <c>services.AddEventModel(name, configure)</c>); it is surfaced as an
/// <see cref="IEventModelDefinitionSource"/> and enumerated by <see cref="EventModelDiscovery"/>.
/// </para>
/// </remarks>
public abstract class EventModelDefinition
{
    /// <summary>
    /// Name of the model this overlay contributes to. Defaults to the defining type's name;
    /// override to contribute to a named model (the merge assembles all sources by model).
    /// </summary>
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Populate <paramref name="builder"/> with the overlay — slice names, domains, trigger
    /// labels, specification links, hotspots. Called once by the discovery layer.
    /// </summary>
    /// <param name="builder">Builder that accumulates the overlay.</param>
    public abstract void Configure(EventModelBuilder builder);
}
