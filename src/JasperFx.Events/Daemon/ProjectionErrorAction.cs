namespace JasperFx.Events.Daemon;

/// <summary>
/// What the async daemon does when a projection fails on an event (marten#6003).
/// </summary>
public enum ProjectionErrorAction
{
    /// <summary>
    /// Pause the shard. Nothing moves until someone fixes the problem and restarts it.
    /// </summary>
    Pause,

    /// <summary>
    /// Skip the event: write a <see cref="DeadLetterEvent" /> for it and carry on with the rest of the batch.
    /// </summary>
    Skip,

    /// <summary>
    /// Park the identity the failing event belongs to: drop that identity's slice from the batch, record a
    /// <see cref="ParkedIdentity" /> row in the same transaction that moves the shard's progression, hold every
    /// later event for that identity until it is replayed or discarded, and carry on with every other identity.
    /// Where an identity can't be parked (a projection that isn't a single-stream aggregation, a composite member, a
    /// rebuild, or a store that doesn't support parking), this falls back to <see cref="Pause" />.
    /// </summary>
    Park
}
