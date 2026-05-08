namespace JasperFx.Descriptors;

/// <summary>
/// Wire-format-neutral mirror of an OpenAPI operation — i.e. the
/// shape of one HTTP endpoint as it would be described in a Swagger /
/// OpenAPI document. No <c>Microsoft.OpenApi</c> types cross the
/// SignalR boundary; this descriptor and its companion sub-records are
/// pure DTOs.
/// </summary>
public class OpenApiOperationDescriptor
{
    /// <summary>Operation id — corresponds to OpenAPI <c>operationId</c>.</summary>
    public string OperationId { get; set; } = "";

    /// <summary>OpenAPI <c>summary</c>.</summary>
    public string? Summary { get; set; }

    /// <summary>OpenAPI <c>description</c>.</summary>
    public string? Description { get; set; }

    /// <summary>True when the operation is marked <c>deprecated</c> in OpenAPI.</summary>
    public bool Deprecated { get; set; }

    /// <summary>OpenAPI <c>tags</c>.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Path / query / header / cookie parameters.</summary>
    public List<OpenApiParameterDescriptor> Parameters { get; set; } = new();

    /// <summary>Request body, when the operation accepts one.</summary>
    public OpenApiRequestBodyDescriptor? RequestBody { get; set; }

    /// <summary>
    /// Responses keyed by status code as a string (<c>"200"</c>,
    /// <c>"default"</c>, etc.) so the wire format mirrors OpenAPI.
    /// </summary>
    public Dictionary<string, OpenApiResponseDescriptor> Responses { get; set; } = new();

    /// <summary>Security schemes that apply to this operation.</summary>
    public List<OpenApiSecurityDescriptor> Security { get; set; } = new();
}

/// <summary>
/// One parameter on an operation — corresponds to a single OpenAPI
/// <c>parameter</c> entry.
/// </summary>
public class OpenApiParameterDescriptor
{
    /// <summary>Parameter name as it appears on the wire.</summary>
    public string Name { get; set; } = "";

    /// <summary>One of <c>"path"</c>, <c>"query"</c>, <c>"header"</c>, <c>"cookie"</c>.</summary>
    public string In { get; set; } = "";

    public string? Description { get; set; }
    public bool Required { get; set; }
    public bool Deprecated { get; set; }

    /// <summary>Schema describing the parameter shape.</summary>
    public OpenApiSchemaDescriptor? Schema { get; set; }

    /// <summary>Example value, when supplied.</summary>
    public object? Example { get; set; }
}

/// <summary>
/// Request body — keyed by media type, each carrying its own schema.
/// </summary>
public class OpenApiRequestBodyDescriptor
{
    public string? Description { get; set; }
    public bool Required { get; set; }

    /// <summary>
    /// Media-type → media-type-object mapping. Mirrors OpenAPI's
    /// <c>content</c> keyed dictionary.
    /// </summary>
    public Dictionary<string, OpenApiMediaTypeDescriptor> Content { get; set; } = new();
}

/// <summary>
/// Single response entry — one per status code on the parent
/// <see cref="OpenApiOperationDescriptor.Responses"/>.
/// </summary>
public class OpenApiResponseDescriptor
{
    public string? Description { get; set; }

    /// <summary>Media-type → media-type-object mapping.</summary>
    public Dictionary<string, OpenApiMediaTypeDescriptor> Content { get; set; } = new();

    /// <summary>Response headers keyed by header name.</summary>
    public Dictionary<string, OpenApiSchemaDescriptor> Headers { get; set; } = new();
}

/// <summary>
/// A single media-type entry — wraps a schema + an optional example.
/// </summary>
public class OpenApiMediaTypeDescriptor
{
    public OpenApiSchemaDescriptor? Schema { get; set; }
    public object? Example { get; set; }
}

/// <summary>
/// Security requirement — the OpenAPI security descriptor knits a
/// scheme name to the scope set the operation needs.
/// </summary>
public class OpenApiSecurityDescriptor
{
    /// <summary>
    /// Scheme name — corresponds to a key under the document's
    /// <c>components.securitySchemes</c>.
    /// </summary>
    public string SchemeName { get; set; } = "";

    /// <summary>
    /// Type of the scheme — <c>"http"</c>, <c>"apiKey"</c>,
    /// <c>"oauth2"</c>, <c>"openIdConnect"</c>.
    /// </summary>
    public string SchemeType { get; set; } = "";

    /// <summary>OAuth2 flow when relevant — <c>"implicit"</c>, <c>"password"</c>, etc.</summary>
    public string? Flow { get; set; }

    /// <summary>Required scopes for this operation.</summary>
    public List<string> Scopes { get; set; } = new();
}
