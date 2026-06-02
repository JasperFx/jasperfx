namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic mirror of a single Wolverine.Http chain. One row per
/// (method + route) — versioned variants collapse into a single
/// descriptor with <see cref="ApiVersion"/> describing the range.
/// </summary>
/// <remarks>
/// Source code is not snapshotted into this descriptor — it stays
/// on-demand via the existing <c>RequestHandlerSourceCode</c> round-
/// trip (the HttpChainDetail page lazy-fetches when the operator opens
/// the Generated Code tab).
/// </remarks>
public class HttpChainDescriptor : OptionsDescription
{
    /// <summary>
    /// Stable hash of (HTTP method + route + operation id) used in the
    /// detail-page URL. Survives chain rebuild as long as the route
    /// stays put.
    /// </summary>
    public string ChainId { get; set; } = "";

    /// <summary>Raw route pattern — e.g. <c>"/orders/{id}"</c>.</summary>
    public string Route { get; set; } = "";

    /// <summary>
    /// HTTP methods this chain answers — typically a single entry, but
    /// e.g. <c>MapMethods</c> can wire multiple verbs.
    /// </summary>
    public List<string> HttpMethods { get; set; } = new();

    /// <summary>Optional ASP.NET Core route name.</summary>
    public string? RouteName { get; set; }

    /// <summary>Endpoint order — affects route ranking.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Display name (typically <c>Type.Method</c>). Falls back to the
    /// generated file name when no explicit name is set.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Operation id — derived from <c>Type.Method</c> by default; set
    /// explicitly via <c>WolverineHttpMethodAttribute.OperationId</c>.
    /// </summary>
    public string OperationId { get; set; } = "";

    /// <summary>
    /// True when the operation id was set explicitly (versus derived).
    /// </summary>
    public bool HasExplicitOperationId { get; set; }

    /// <summary>Endpoint summary (OpenAPI <c>summary</c>).</summary>
    public string? EndpointSummary { get; set; }

    /// <summary>Endpoint description (OpenAPI <c>description</c>).</summary>
    public string? EndpointDescription { get; set; }

    /// <summary>Full name of the handler type.</summary>
    public string EndpointTypeFullName { get; set; } = "";

    /// <summary>Method name on the handler.</summary>
    public string MethodName { get; set; } = "";

    /// <summary>Compact method signature, e.g. <c>Get(Guid id)</c>.</summary>
    public string MethodSignature { get; set; } = "";

    /// <summary>Request-body type, when the chain consumes one.</summary>
    public TypeDescriptor? RequestType { get; set; }

    /// <summary>Resource (response) type, when not <c>void</c>.</summary>
    public TypeDescriptor? ResourceType { get; set; }

    /// <summary>True when the chain accepts <c>application/x-www-form-urlencoded</c> / <c>multipart/form-data</c>.</summary>
    public bool IsFormData { get; set; }

    /// <summary>True when the chain returns 204 No Content.</summary>
    public bool NoContent { get; set; }

    /// <summary>True when the chain requires the Wolverine outbox (i.e. depends on <c>IMessageBus</c> / <c>MessageContext</c>).</summary>
    public bool RequiresOutbox { get; set; }

    /// <summary>
    /// Content-negotiation mode as a string — <c>"Loose"</c> /
    /// <c>"Strict"</c>.
    /// </summary>
    public string? ConnegMode { get; set; }

    /// <summary>
    /// Service-provider source mode as a string — <c>"IsolatedAndScoped"</c>
    /// / <c>"FromHttpContext"</c>.
    /// </summary>
    public string? ServiceProviderSource { get; set; }

    /// <summary>
    /// Required tenancy mode as a string — <c>"None"</c> / <c>"Required"</c>
    /// / <c>"Optional"</c>. Null when not declared on the chain.
    /// </summary>
    public string? TenancyMode { get; set; }

    /// <summary>
    /// API version metadata — declared version, deprecation flag, range
    /// when collapsed across version clones.
    /// </summary>
    public ApiVersionDescriptor? ApiVersion { get; set; }

    /// <summary>OpenAPI tags applied to this chain.</summary>
    public new List<string> Tags { get; set; } = new();

    // jasperfx#411: the Middleware / Postprocessors / ServiceDependencies pipeline-introspection fields
    // were removed. The same operator-facing information is available on demand via the chain's generated
    // source code (RequestHandlerSourceCode in Wolverine.CritterWatch) — the generated C# IS the compiled
    // pipeline — so the descriptor copies were redundant payload.

    /// <summary>
    /// Cascading message types this chain may publish — the
    /// non-resource members of <c>MethodCall.Creates</c>. Frontend
    /// renders these as clickable chips into the messaging detail page.
    /// </summary>
    public List<TypeDescriptor> CascadingMessageTypes { get; set; } = new();

    /// <summary>Full OpenAPI shape of the operation, when discoverable.</summary>
    public OpenApiOperationDescriptor? OpenApi { get; set; }

    /// <summary>
    /// True when the chain is marked transactional via
    /// <c>[Transactional]</c> attribute or runtime policy, surfacing the
    /// Q8-#102 transactional override.
    /// </summary>
    public bool IsTransactional { get; set; }
}
