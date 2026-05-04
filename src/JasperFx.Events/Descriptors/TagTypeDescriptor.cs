using JasperFx.Descriptors;

namespace JasperFx.Events.Descriptors;

/// <summary>
/// Diagnostic mirror of an <c>ITagTypeRegistration</c>. Carries the configuration
/// shape only — no per-tag row counts or runtime state — so the descriptor can be
/// safely serialized to and from monitoring tools (e.g. CritterWatch) without
/// pulling in the live registration's behaviour.
/// </summary>
public class TagTypeDescriptor
{
    /// <summary>
    /// FullName of the strong-typed identifier type (e.g. <c>"MyApp.StudentId"</c>).
    /// </summary>
    public string TagType { get; set; } = "";

    /// <summary>
    /// FullName of the inner primitive type the tag wraps (e.g. <c>"System.String"</c>,
    /// <c>"System.Guid"</c>).
    /// </summary>
    public string SimpleType { get; set; } = "";

    /// <summary>
    /// Table-name suffix Marten / Polecat uses for the per-tag-type table
    /// (e.g. <c>"student_id"</c>).
    /// </summary>
    public string TableSuffix { get; set; } = "";

    /// <summary>
    /// FullName of the linked aggregate type when the tag has been bound to a specific
    /// aggregate via <c>ForAggregate</c>; <see langword="null"/> when unbound.
    /// </summary>
    public string? AggregateType { get; set; }
}
