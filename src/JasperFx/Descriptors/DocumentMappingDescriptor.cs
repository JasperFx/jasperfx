namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic mirror of a single document-type mapping (Marten's
/// <c>DocumentMapping</c>, Polecat's equivalent). Carries the operationally-
/// interesting configuration shape plus the full DDL the store will emit for
/// the type — operators on a monitoring console want to see at a glance the
/// table, identity strategy, soft-delete semantics, and the actual schema
/// that will be applied.
/// </summary>
/// <remarks>
/// Index and foreign-key counts are intentionally absent: the <see cref="Ddl"/>
/// field already contains the full <c>CREATE TABLE</c> + index + FK
/// statements, so duplicating count fields just invites drift between the
/// surface counters and the canonical DDL.
/// </remarks>
public class DocumentMappingDescriptor
{
    /// <summary>
    /// CLR identity of the document type — name / fullname / assembly name.
    /// </summary>
    public TypeDescriptor DocumentType { get; set; } = null!;

    /// <summary>
    /// Resolved schema name where the document table lives. Falls back to the
    /// store-wide <c>DatabaseSchemaName</c> when not overridden per type.
    /// </summary>
    public string DatabaseSchemaName { get; set; } = "";

    /// <summary>
    /// Document table-name suffix (Marten's <c>DocumentMapping.Alias</c>) — the
    /// short identifier used to derive the table name (e.g. <c>"trip"</c> →
    /// <c>{schema}.mt_doc_trip</c>).
    /// </summary>
    public string Alias { get; set; } = "";

    /// <summary>
    /// CLR type name of the <c>IIdGeneration</c> implementation backing this
    /// mapping (e.g. <c>"HiloIdGeneration"</c>, <c>"GuidIdGeneration"</c>,
    /// <c>"IdentityKeyGeneration"</c>).
    /// </summary>
    public string IdStrategy { get; set; } = "";

    /// <summary>
    /// <c>"Single"</c> or <c>"Conjoined"</c>. Mirrors <c>TenancyStyle</c> as
    /// a string for serialization-friendliness across version skews.
    /// </summary>
    public string TenancyStyle { get; set; } = "";

    /// <summary>
    /// <c>"Remove"</c>, <c>"SoftDelete"</c>, or <c>"SoftDeleteWithPartitioning"</c>.
    /// Mirrors <c>DeleteStyle</c> as a string.
    /// </summary>
    public string DeleteStyle { get; set; } = "";

    /// <summary>
    /// Whether the document table carries a <c>version</c> column for
    /// optimistic-concurrency checks.
    /// </summary>
    public bool UseOptimisticConcurrency { get; set; }

    /// <summary>
    /// Whether the document uses numeric revision tokens (vs guid versions) for
    /// optimistic concurrency. Mostly relevant to integrators interoperating
    /// with non-Marten consumers.
    /// </summary>
    public bool UseNumericRevisions { get; set; }

    /// <summary>
    /// Number of subclass mappings registered against this aggregate root —
    /// non-zero indicates a hierarchy (<c>doc_type</c> column is present).
    /// </summary>
    public int SubClassCount { get; set; }

    /// <summary>
    /// CLR type name of the <c>IPartitionStrategy</c> in effect, or
    /// <see langword="null"/> when the table is not partitioned.
    /// </summary>
    public string? PartitioningStrategy { get; set; }

    /// <summary>
    /// Full DDL the store will generate for this document type — table
    /// definition, indexes, foreign keys, and any partition declarations.
    /// Render this verbatim on the operator console as the canonical "what
    /// schema gets applied" view.
    /// </summary>
    public string Ddl { get; set; } = "";
}
