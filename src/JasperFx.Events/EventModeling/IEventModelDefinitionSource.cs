namespace JasperFx.Events.EventModeling;

/// <summary>
/// Discovery interface registered by application code (or a generated
/// source) to surface an <see cref="EventModelDescriptor"/> for the
/// CritterWatch swim-lane and other Event Modeling consumers. One
/// implementation per modelled topology; the host enumerates all
/// registered sources to assemble the full picture.
/// </summary>
public interface IEventModelDefinitionSource
{
    /// <summary>
    /// Stable URI identifying the modelled topology within the host
    /// process. A common scheme is <c>event-model://{name}</c>; multi-bounded-context
    /// apps distinguish by the bounded-context name in the path.
    /// </summary>
    Uri Subject { get; }

    /// <summary>
    /// Which rung of the provenance ladder this source's claims sit on (jasperfx#703).
    /// <see cref="EventModelProvenance.Declared"/> by default, because that is what an overlay or a
    /// spec is; a source that reads roles out of code overrides it with
    /// <see cref="EventModelProvenance.Derived"/>, and one that watches a running system with
    /// <see cref="EventModelProvenance.Observed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EventModelDiscovery.DiscoverAsync"/> stamps this onto every slice the source
    /// returns that is not already attributed, so a source only has to answer this once rather than
    /// tag each slice.
    /// </para>
    /// <para>
    /// ⚠️ This replaces registration order as the way derived roles beat declared ones. Defaulting to
    /// <see cref="EventModelProvenance.Declared"/> means every existing source ties, and a tie still
    /// resolves on order — so nothing changes until a source says otherwise.
    /// </para>
    /// </remarks>
    EventModelProvenance Provenance => EventModelProvenance.Declared;

    /// <summary>
    /// Build an <see cref="EventModelDescriptor"/> for this source.
    /// Returns <see langword="null"/> when the source cannot produce a
    /// descriptor — e.g. the underlying definition type failed to
    /// resolve from the supplied service provider.
    /// </summary>
    /// <param name="services">Service provider used to resolve any dependencies the underlying definition declares.</param>
    /// <param name="token">Cancellation token.</param>
    Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token);
}
