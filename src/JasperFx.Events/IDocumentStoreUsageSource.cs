using JasperFx.Descriptors;

namespace JasperFx.Events;

/// <summary>
/// Implemented by document stores (Marten's <c>IDocumentStore</c>, Polecat's
/// equivalent) to expose a diagnostic snapshot of their configuration shape
/// for monitoring tools (CritterWatch). The parallel of
/// <see cref="IEventStore.TryCreateUsage"/> for the document side; lives in
/// JasperFx.Events alongside <c>IEventStore</c> so consumers (Wolverine's
/// <c>ServiceCapabilities</c>, monitoring agents) can discover both shapes
/// from one assembly.
/// </summary>
/// <remarks>
/// The descriptor type itself (<see cref="DocumentStoreUsage"/>) lives in
/// JasperFx core because it carries no event-specific concepts; only the
/// discovery interface is rooted in JasperFx.Events to mirror
/// <c>IEventStore</c>'s placement.
/// </remarks>
public interface IDocumentStoreUsageSource
{
    /// <summary>
    /// Stable URI identifying this document store within the host process —
    /// the same identity Marten / Polecat use for their event-store
    /// <see cref="IEventStore.Subject"/>. Common scheme is
    /// <c>marten://main</c> or <c>polecat://main</c>; ancillary stores carry
    /// their registered name in the path.
    /// </summary>
    Uri Subject { get; }

    /// <summary>
    /// Build a <see cref="DocumentStoreUsage"/> snapshot for this store.
    /// Returns <see langword="null"/> when the store can't produce a usage
    /// description (e.g. transient initialization failure).
    /// </summary>
    Task<DocumentStoreUsage?> TryCreateUsage(CancellationToken token);
}
