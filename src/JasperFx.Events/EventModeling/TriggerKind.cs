namespace JasperFx.Events.EventModeling;

/// <summary>
/// What starts a slice (jasperfx#687 decision 8). Finer-grained than the
/// "human / external" split of textbook Event Modeling because the Critter
/// Stack can tell its own trigger kinds apart statically.
/// </summary>
/// <remarks>
/// Derived by the producing source, never declared through the overlay. A slice
/// whose trigger kind has not been derived carries <see langword="null"/>.
/// </remarks>
public enum TriggerKind
{
    /// <summary>An HTTP endpoint (e.g. a Wolverine.Http route). Route and verb live on the slice's trigger origin.</summary>
    Http,

    /// <summary>A gRPC service method. Service and method live on the slice's trigger origin.</summary>
    Grpc,

    /// <summary>An inbound message handled by a Wolverine message handler; the command type is the trigger.</summary>
    MessageHandler,

    /// <summary>
    /// A scheduled or recurring job. Reserved ahead of the Critter Stack job scheduler landing;
    /// sources may stamp it today for cron-style / scheduled publishers.
    /// </summary>
    JobScheduler,

    /// <summary>A person acting through a UI / wireframe.</summary>
    Human,

    /// <summary>An external system pushing into the model (the inbound half of a translation slice).</summary>
    External,
}
