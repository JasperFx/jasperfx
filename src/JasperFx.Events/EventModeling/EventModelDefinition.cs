namespace JasperFx.Events.EventModeling;

/// <summary>
/// Base class application authors derive from to declare an Event Model in code: slices, their roles
/// (by CLR type or by name), domains, chapters, aggregates, links to specifications and the questions
/// still open. Override <see cref="Configure"/> to populate the supplied <see cref="EventModelBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design-first, over stub types.</b> Write stub records for the commands, events, aggregates and
/// views first, declare the model against them here, and write the specifications against the same
/// stubs. Everything declared sits on the <see cref="EventModelProvenance.Declared"/> rung of the
/// provenance ladder (jasperfx#703): once Wolverine or another source derives a role from code, the
/// derived claim wins, and any difference renders as a <see cref="HotspotOrigin.SourceDisagreement"/>
/// hotspot — the gap between the model and the code. Before jasperfx#703 a declaration could overwrite
/// what the code did, which is why jasperfx#687 had cut this down to a names-only overlay; jasperfx#957
/// restored role declarations on top of the ladder.
/// </para>
/// <para>
/// Register a definition with <c>services.AddEventModel&lt;TDefinition&gt;()</c>, every definition in an
/// assembly with <c>services.AddDiscoveredEventModels(assembly)</c> (jasperfx#993), or an inline
/// lambda via <c>services.AddEventModel(name, configure)</c>); it is surfaced as an
/// <see cref="IEventModelDefinitionSource"/>, enumerated by <see cref="EventModelDiscovery"/>, and its
/// slices carry the definition as their <see cref="EventModelSliceDescriptor.Origin"/> (jasperfx#959).
/// </para>
/// </remarks>
public abstract class EventModelDefinition
{
    /// <summary>
    /// Name of the model this definition contributes to. Null — the default — means <em>the
    /// application's model</em>, resolved at discovery by
    /// <see cref="EventModelDiscovery.ApplicationModelName"/>: the service name, which is what Wolverine
    /// and the stores name their code-derived models. Override only for an app that genuinely hosts
    /// several models; the merge assembles sources by model name.
    /// </summary>
    /// <remarks>
    /// Before jasperfx#992 this defaulted to the defining type's name, so a definition merged with the
    /// code only when its class happened to be named for the service. One definition per chapter — a
    /// <c>BookingAppointmentsModel</c> — became a separate model, and none of its declarations met the
    /// slices Wolverine derived. Which definition declared a slice is still recorded, per definition, on
    /// <see cref="EventModelSliceDescriptor.Origin"/> (jasperfx#959).
    /// </remarks>
    public virtual string? Name => null;

    /// <summary>
    /// Populate <paramref name="builder"/> with the overlay — slice names, domains, trigger
    /// labels, specification links, hotspots. Called once by the discovery layer.
    /// </summary>
    /// <param name="builder">Builder that accumulates the overlay.</param>
    public abstract void Configure(EventModelBuilder builder);
}
