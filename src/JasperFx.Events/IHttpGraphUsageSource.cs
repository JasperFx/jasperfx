using JasperFx.Descriptors;

namespace JasperFx.Events;

/// <summary>
/// Implemented by Wolverine.Http (and other HTTP integrations) to
/// expose a diagnostic snapshot of the HTTP graph for monitoring tools
/// (CritterWatch). Mirrors <see cref="IDocumentStoreUsageSource"/> on
/// the HTTP side; lives in JasperFx.Events for the same reason — DI
/// consumers (Wolverine's <c>ServiceCapabilities</c>) discover all
/// shapes from one assembly.
/// </summary>
/// <remarks>
/// Discovery interface only — the descriptor type
/// (<see cref="HttpGraphUsage"/>) lives in JasperFx core because it
/// carries no event-specific concepts.
/// </remarks>
public interface IHttpGraphUsageSource
{
    /// <summary>
    /// Stable URI identifying this HTTP graph within the host process.
    /// Common scheme is <c>wolverine-http://main</c>; multiple HTTP
    /// graphs (rare today, but plausible long-term) carry their
    /// registered name in the path.
    /// </summary>
    Uri Subject { get; }

    /// <summary>
    /// Build an <see cref="HttpGraphUsage"/> snapshot for this HTTP
    /// graph. Receives the host service provider so the implementation
    /// can resolve <c>IApiDescriptionGroupCollectionProvider</c> and
    /// other ASP.NET Core diagnostic services. Returns
    /// <see langword="null"/> when the graph can't produce a usage
    /// description (e.g. transient initialization failure).
    /// </summary>
    Task<HttpGraphUsage?> TryCreateUsage(IServiceProvider services, CancellationToken token);
}
