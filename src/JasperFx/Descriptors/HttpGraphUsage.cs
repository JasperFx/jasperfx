namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic snapshot of a Wolverine.HTTP graph for a single service —
/// the parallel of <see cref="DocumentStoreUsage"/> on the HTTP side.
/// Surfaces the operationally-interesting Wolverine.Http configuration
/// (route prefix, antiforgery defaults, namespace prefixes, applied
/// policies, middleware types, tenant detection strategies, API
/// versioning enablement) plus the full per-chain inventory.
/// </summary>
/// <remarks>
/// <para>
/// Wire-format-neutral — no <c>Microsoft.OpenApi</c> types cross the
/// SignalR boundary. OpenAPI shape is captured via the descriptor sub-
/// records (<see cref="OpenApiOperationDescriptor"/>,
/// <see cref="OpenApiSchemaDescriptor"/>) which are pure DTOs.
/// </para>
/// <para>
/// Populated by <c>IHttpGraphUsageSource</c> implementations registered
/// in DI (currently a single source inside <c>Wolverine.Http</c> that
/// auto-registers when <c>MapWolverineEndpoints()</c> is called).
/// </para>
/// </remarks>
public class HttpGraphUsage : OptionsDescription
{
    /// <summary>
    /// Stable URI identifying this HTTP graph within the host process —
    /// e.g. <c>wolverine-http://main</c>.
    /// </summary>
    public Uri SubjectUri { get; set; } = null!;

    /// <summary>
    /// Service name (mirrors <c>ServiceCapabilities</c>) so frontend
    /// can correlate when it merges across services.
    /// </summary>
    public string ServiceName { get; set; } = "";

    /// <summary>
    /// Version of the Wolverine.Http assembly that produced this snapshot.
    /// </summary>
    public string? WolverineHttpVersion { get; set; }

    /// <summary>
    /// <c>WolverineHttpOptions.WarmUpRoutes</c> mode as a string —
    /// <c>"Eager"</c> / <c>"Lazy"</c> / etc.
    /// </summary>
    public string? WarmUpRoutes { get; set; }

    /// <summary>
    /// <c>WolverineHttpOptions.ServiceProviderSource</c> as a string.
    /// </summary>
    public string? ServiceProviderSource { get; set; }

    /// <summary>
    /// True when antiforgery is auto-applied to form/file endpoints.
    /// </summary>
    public bool AutoAntiforgeryOnFormEndpoints { get; set; }

    /// <summary>
    /// Global route prefix applied to every chain — e.g. <c>"/api"</c>.
    /// Null when no prefix policy is configured.
    /// </summary>
    public string? GlobalRoutePrefix { get; set; }

    /// <summary>
    /// Namespace-prefix policies — narrow per-namespace prefix overrides
    /// applied through <c>RoutePrefixPolicy</c>.
    /// </summary>
    public List<NamespacePrefixDescriptor> NamespacePrefixes { get; set; } = new();

    /// <summary>
    /// Names of resource-writer policies in apply order — e.g.
    /// <c>EmptyBody204Policy</c>, <c>StatusCodePolicy</c>,
    /// <c>JsonResourceWriterPolicy</c>.
    /// </summary>
    public List<string> ResourceWriterPolicyNames { get; set; } = new();

    /// <summary>
    /// Names of <c>IChainPolicy</c> implementations in apply order.
    /// </summary>
    public List<string> PolicyNames { get; set; } = new();

    // jasperfx#411: graph-level MiddlewareTypes (the WolverineHttpOptions.Middleware registry) was removed
    // for API consistency with the per-chain pipeline-introspection removal.

    /// <summary>
    /// Names of tenant-detection strategies registered on the graph.
    /// </summary>
    public List<string> TenantDetectionStrategies { get; set; } = new();

    /// <summary>
    /// True when <c>WolverineHttpOptions.ApiVersioning</c> is set.
    /// </summary>
    public bool ApiVersioningEnabled { get; set; }

    /// <summary>
    /// Per-chain descriptors — the heart of the snapshot. Versioned
    /// chains are collapsed into a single descriptor with
    /// <see cref="HttpChainDescriptor.ApiVersion"/> describing the
    /// version range.
    /// </summary>
    public List<HttpChainDescriptor> Chains { get; set; } = new();
}
