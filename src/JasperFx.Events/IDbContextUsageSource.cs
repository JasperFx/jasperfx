using JasperFx.Descriptors;

namespace JasperFx.Events;

/// <summary>
/// Implemented by EF Core <c>DbContext</c> bridges to expose a diagnostic
/// snapshot of their configuration shape for monitoring tools (CritterWatch,
/// #102). The EF Core parallel of <see cref="IDocumentStoreUsageSource"/>
/// for the document side and <see cref="IEventStore.TryCreateUsage"/> for
/// the event-store side.
/// </summary>
/// <remarks>
/// <para>
/// Lives in JasperFx.Events alongside <see cref="IEventStore"/> and
/// <see cref="IDocumentStoreUsageSource"/> so consumers (Wolverine's
/// <c>ServiceCapabilities</c>, monitoring agents) can discover all three
/// shapes from one assembly via a single
/// <c>GetServices&lt;IDbContextUsageSource&gt;()</c> call.
/// </para>
/// <para>
/// The descriptor type itself (<see cref="DbContextUsage"/>) lives in
/// JasperFx core because it carries no event-specific concepts; only the
/// discovery interface is rooted in JasperFx.Events to mirror
/// <see cref="IDocumentStoreUsageSource"/>'s placement.
/// </para>
/// </remarks>
public interface IDbContextUsageSource
{
    /// <summary>
    /// Stable URI identifying this DbContext within the host process. Common
    /// scheme is <c>efcore://OrdersDbContext</c>; multi-context apps
    /// distinguish by the context type-name in the path.
    /// </summary>
    Uri Subject { get; }

    /// <summary>
    /// Build a <see cref="DbContextUsage"/> snapshot for this context.
    /// Returns <see langword="null"/> when the context can't produce a usage
    /// description (e.g. transient connection failure, provider doesn't
    /// support model introspection).
    /// </summary>
    Task<DbContextUsage?> TryCreateUsage(CancellationToken token);
}
