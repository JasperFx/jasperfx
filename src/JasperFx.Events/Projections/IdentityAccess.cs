namespace JasperFx.Events.Projections;

/// <summary>
/// How an async projection finds the events of one of its identities (marten#6003). That decides whether a failing
/// identity can be parked, replayed and rebuilt on its own, and whether the stepper can read one identity's events
/// exactly.
/// </summary>
public enum IdentityAccessClass
{
    /// <summary>
    /// A single-stream projection: an identity's events are its stream. Parks, replays and rebuilds one identity.
    /// </summary>
    SingleStream,

    /// <summary>
    /// A multi-stream projection whose every grouping rule is <c>IdentityFromStream&lt;TEvent&gt;()</c>: the identity is
    /// the event's stream, so its events are found by stream, exactly.
    /// </summary>
    Stream,

    /// <summary>
    /// A multi-stream projection whose every grouping rule is <c>IdentityFromTag&lt;TTag&gt;()</c> (or
    /// <c>IdentityFromStream&lt;TEvent&gt;()</c>): the identity is a stored DCB tag value, so its events are found through
    /// the tag, exactly.
    /// </summary>
    Tag,

    /// <summary>
    /// A multi-stream projection that keeps a slice index: the daemon records which identities each event went to,
    /// so an identity's events are found through the index. Not available yet.
    /// </summary>
    SliceIndex,

    /// <summary>
    /// A multi-stream projection whose grouping can't be inverted: plain <c>Identity</c>/<c>Identities</c> delegates,
    /// custom grouping, a custom slicer, or rolling up by tenant. An identity's events can only be found by scanning
    /// every event of the projection's types, so it doesn't park: a failure falls back to pausing the shard.
    /// </summary>
    ScanOnly,

    /// <summary>
    /// Not an identity-sliced projection, or one that doesn't park in this version: event projections, flat-table
    /// projections, subscriptions, and composite projections and their members.
    /// </summary>
    NotSupported
}

/// <summary>
/// How an async projection finds one identity's events, and why (marten#6003). Computed when the projection is
/// registered, reported by <c>describe</c>, and logged by the daemon when a projection set to park can't.
/// </summary>
/// <param name="Class">The class.</param>
/// <param name="Reason">Why the projection is in this class, and for <see cref="IdentityAccessClass.ScanOnly" />
/// what to change.</param>
public sealed record IdentityAccess(IdentityAccessClass Class, string Reason)
{
    /// <summary>Can one identity's events be found exactly, without a scan?</summary>
    public bool Exact => Class is IdentityAccessClass.SingleStream or IdentityAccessClass.Stream
        or IdentityAccessClass.Tag or IdentityAccessClass.SliceIndex;

    /// <summary>
    /// For <see cref="IdentityAccessClass.SliceIndex" />, the sequence the index is complete from. Null otherwise.
    /// </summary>
    public long? CompleteFrom { get; init; }

    /// <summary>
    /// Does the daemon park a failing identity of this projection? Only a single-stream projection, in this version.
    /// Every other class falls back to pausing the shard when the error action is
    /// <see cref="Daemon.ProjectionErrorAction.Park" />.
    /// </summary>
    // Phase 2 of marten#6003 widens this to the exact multi-stream classes
    public bool CanPark => Class == IdentityAccessClass.SingleStream;

    public static IdentityAccess SingleStream { get; } = new(IdentityAccessClass.SingleStream,
        "A single-stream projection: an identity's events are its stream.");

    public static IdentityAccess NotSupported(string reason) => new(IdentityAccessClass.NotSupported, reason);

    public static IdentityAccess ScanOnly(string reason) => new(IdentityAccessClass.ScanOnly, reason);

    public override string ToString() => $"{Class}: {Reason}";
}

/// <summary>
/// A projection that can say how it finds one identity's events (marten#6003).
/// </summary>
public interface IHasIdentityAccess
{
    IdentityAccess IdentityAccess { get; }
}
