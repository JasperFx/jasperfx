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
    /// Build an <see cref="EventModelDescriptor"/> for this source.
    /// Returns <see langword="null"/> when the source cannot produce a
    /// descriptor — e.g. the underlying definition type failed to
    /// resolve from the supplied service provider.
    /// </summary>
    /// <param name="services">Service provider used to resolve any dependencies the underlying definition declares.</param>
    /// <param name="token">Cancellation token.</param>
    Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token);
}
