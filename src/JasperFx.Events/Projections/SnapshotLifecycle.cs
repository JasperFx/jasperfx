namespace JasperFx.Events.Projections;

/// <summary>
///     Lifecycle for snapshot (self-aggregating) projections. A snapshot
///     projection runs either inline with the transaction that captures
///     events, or asynchronously inside the projection daemon.
/// </summary>
/// <remarks>
///     This is a deliberate subset of <see cref="ProjectionLifecycle" />:
///     <list type="bullet">
///         <item>
///             <see cref="Inline" /> maps to <see cref="ProjectionLifecycle.Inline" />.
///         </item>
///         <item>
///             <see cref="Async" /> maps to <see cref="ProjectionLifecycle.Async" />.
///         </item>
///         <item>
///             <see cref="ProjectionLifecycle.Live" /> has no snapshot counterpart —
///             a live projection is computed on demand and has no persisted
///             snapshot to update.
///         </item>
///     </list>
///     Each consuming product (Marten, Polecat) maintains its own
///     <c>SnapshotLifecycle → ProjectionLifecycle</c> mapping in the
///     projection-registration layer, since the broader
///     <see cref="ProjectionLifecycle" /> enum is a product-registration
///     concern rather than a shared event-sourcing concept.
/// </remarks>
public enum SnapshotLifecycle
{
    /// <summary>
    ///     The snapshot will be updated in the same transaction as the
    ///     events being captured.
    /// </summary>
    Inline,

    /// <summary>
    ///     The snapshot will be updated asynchronously inside the
    ///     projection daemon.
    /// </summary>
    Async
}
