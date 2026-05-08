namespace JasperFx.Descriptors;

/// <summary>
/// Recursive description of an OpenAPI schema. Walks the full shape
/// up to a configurable depth (depth-3 inline + <c>$ref</c> chip beyond
/// — Q12) so cyclic types (linked-list nodes, tree nodes, owning
/// document with self-reference) don't blow the wire format.
/// </summary>
/// <remarks>
/// Wire-format-neutral. No <c>Microsoft.OpenApi</c> types involved —
/// the discovery side flattens an <c>OpenApiSchema</c> into this DTO.
/// Frontend renders lazily-expandable trees keyed by <see cref="Ref"/>
/// once depth is exceeded.
/// </remarks>
public class OpenApiSchemaDescriptor
{
    /// <summary>OpenAPI <c>type</c> (<c>"object"</c>, <c>"string"</c>, <c>"integer"</c>, etc.).</summary>
    public string? Type { get; set; }

    /// <summary>OpenAPI <c>format</c> (<c>"int32"</c>, <c>"date-time"</c>, <c>"uuid"</c>, etc.).</summary>
    public string? Format { get; set; }

    /// <summary>OpenAPI <c>title</c>.</summary>
    public string? Title { get; set; }

    /// <summary>OpenAPI <c>description</c>.</summary>
    public string? Description { get; set; }

    /// <summary>Required property names (when <see cref="Type"/> is <c>object</c>).</summary>
    public List<string> Required { get; set; } = new();

    /// <summary>Property name → schema. Empty for non-object types.</summary>
    public Dictionary<string, OpenApiSchemaDescriptor> Properties { get; set; } = new();

    /// <summary>Item schema for <c>array</c> types.</summary>
    public OpenApiSchemaDescriptor? Items { get; set; }

    /// <summary>OpenAPI <c>oneOf</c>.</summary>
    public List<OpenApiSchemaDescriptor> OneOf { get; set; } = new();

    /// <summary>OpenAPI <c>anyOf</c>.</summary>
    public List<OpenApiSchemaDescriptor> AnyOf { get; set; } = new();

    /// <summary>OpenAPI <c>allOf</c>.</summary>
    public List<OpenApiSchemaDescriptor> AllOf { get; set; } = new();

    /// <summary>OpenAPI <c>enum</c> values.</summary>
    public List<object> Enum { get; set; } = new();

    /// <summary>
    /// <c>$ref</c> placeholder — set when expansion is truncated by the
    /// depth limiter. The frontend renders this as a chip the operator
    /// can click to fetch the full sub-tree on demand.
    /// </summary>
    public string? Ref { get; set; }

    /// <summary>OpenAPI <c>nullable</c>.</summary>
    public bool Nullable { get; set; }

    /// <summary>OpenAPI <c>example</c>.</summary>
    public object? Example { get; set; }

    /// <summary>OpenAPI <c>default</c>.</summary>
    public object? Default { get; set; }
}
