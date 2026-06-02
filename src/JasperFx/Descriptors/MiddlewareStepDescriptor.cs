namespace JasperFx.Descriptors;

// jasperfx#411: MiddlewareStepDescriptor (one step in a chain's middleware/postprocessor pipeline) was
// removed along with HttpChainDescriptor.Middleware/Postprocessors. Pipeline information is available on
// demand via the chain's generated source code rather than mirrored into the descriptor payload.

/// <summary>
/// Narrow per-namespace prefix applied via Wolverine's
/// <c>RoutePrefixPolicy</c>.
/// </summary>
public class NamespacePrefixDescriptor
{
    /// <summary>Namespace this prefix targets — e.g. <c>"Api.V2"</c>.</summary>
    public string Namespace { get; set; } = "";

    /// <summary>Route prefix to apply — e.g. <c>"/v2"</c>.</summary>
    public string Prefix { get; set; } = "";
}

/// <summary>
/// API-version metadata for a chain — declared version, deprecation,
/// and (when collapsed across version clones) the version range.
/// </summary>
public class ApiVersionDescriptor
{
    /// <summary>String form of the declared version — e.g. <c>"2"</c>, <c>"2024-01-01"</c>.</summary>
    public string? Version { get; set; }

    /// <summary>True when the chain is marked version-neutral.</summary>
    public bool IsNeutral { get; set; }

    /// <summary>True when the chain (or this version of it) is deprecated.</summary>
    public bool IsDeprecated { get; set; }

    /// <summary>Sunset date when configured.</summary>
    public DateTimeOffset? Sunset { get; set; }

    /// <summary>
    /// All versions covered by this chain when version clones have been
    /// collapsed. Frontend renders one chip per entry.
    /// </summary>
    public List<string> Versions { get; set; } = new();
}
