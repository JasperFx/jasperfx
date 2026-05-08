namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic mirror of a non-Wolverine ASP.NET Core endpoint —
/// Minimal API, MVC controller action, Razor Page, SignalR hub method,
/// or other route mapped via <c>EndpointDataSource</c>. Used to surface
/// the full HTTP surface of a service in CritterWatch alongside the
/// Wolverine.Http chains.
/// </summary>
/// <remarks>
/// Wolverine.Http chains are NOT represented here — they get their
/// own <see cref="HttpChainDescriptor"/> with richer detail. This
/// descriptor covers everything else.
/// </remarks>
public class AspNetEndpointDescriptor : OptionsDescription
{
    /// <summary>
    /// Stable id for the endpoint (hash of method+route+source) used in
    /// the detail-page URL.
    /// </summary>
    public string EndpointId { get; set; } = "";

    /// <summary>Raw route pattern — e.g. <c>"/health"</c>.</summary>
    public string Route { get; set; } = "";

    /// <summary>HTTP methods accepted by this endpoint.</summary>
    public List<string> HttpMethods { get; set; } = new();

    /// <summary>Display name from <c>EndpointNameMetadata</c>.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Discriminator describing the source of the endpoint. One of
    /// <c>"MinimalApi"</c>, <c>"Mvc"</c>, <c>"RazorPages"</c>,
    /// <c>"SignalR"</c>, <c>"StaticFile"</c>, <c>"Other"</c>.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>API description group name, when present.</summary>
    public string? GroupName { get; set; }

    /// <summary>OpenAPI tags applied to this endpoint.</summary>
    public new List<string> Tags { get; set; } = new();

    /// <summary>
    /// Full OpenAPI shape, when discoverable through ApiExplorer or
    /// <c>Microsoft.AspNetCore.OpenApi</c> document service. Null when
    /// the endpoint is opaque to ApiExplorer (e.g. SignalR hubs).
    /// </summary>
    public OpenApiOperationDescriptor? OpenApi { get; set; }

    /// <summary>True when the endpoint requires authorization.</summary>
    public bool RequiresAuthorization { get; set; }

    /// <summary>Authorization policy names applied via <c>[Authorize]</c>.</summary>
    public List<string> AuthorizationPolicies { get; set; } = new();

    /// <summary>Allowed roles applied via <c>[Authorize(Roles=...)]</c>.</summary>
    public List<string> AllowedRoles { get; set; } = new();
}
