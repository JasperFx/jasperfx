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
    /// Retained for back-compat; <see cref="SubClasses"/> carries the list.
    /// </summary>
    public int SubClassCount { get; set; }

    /// <summary>
    /// The subclass-mapping document types registered against this root, so a
    /// console can render the hierarchy rather than just a "has subclasses"
    /// hint. Empty when the type is not a hierarchy root.
    /// </summary>
    public TypeDescriptor[] SubClasses { get; set; } = [];

    /// <summary>
    /// CLR type name of the <c>IPartitionStrategy</c> in effect, or
    /// <see langword="null"/> when the table is not partitioned. Retained for
    /// back-compat; <see cref="Partitioning"/> carries the structured shape.
    /// </summary>
    public string? PartitioningStrategy { get; set; }

    /// <summary>
    /// Structured partitioning shape (strategy + declared partition names), or
    /// <see langword="null"/> when the table is not partitioned. Lets the
    /// console render the actual partitions, not just the strategy label.
    /// </summary>
    public PartitioningDescriptor? Partitioning { get; set; }

    /// <summary>
    /// Full DDL the store will generate for this document type — table
    /// definition, indexes, foreign keys, and any partition declarations.
    /// Render this verbatim on the operator console as the canonical "what
    /// schema gets applied" view.
    /// </summary>
    public string Ddl { get; set; } = "";

    /// <summary>
    /// The indexes on this document table in structured form (jasperfx#870 §5), so a console can say
    /// whether a filter on a member can use one without parsing <see cref="Ddl"/>. Empty either when the
    /// table has no secondary index or when the implementation did not populate it — <see cref="IndexesReported"/>
    /// tells the two apart. <see cref="Ddl"/> remains the canonical view.
    /// </summary>
    public List<DocumentIndexDescriptor> Indexes { get; set; } = new();

    /// <summary>
    /// True when the store populated <see cref="Indexes"/> completely, so an empty list means the table has
    /// no secondary index and a filter on a document member reads every row (jasperfx#1081). False — the
    /// default, and what a store built against a JasperFx older than 2.95 leaves — means "not reported", and
    /// an empty list says nothing.
    /// </summary>
    /// <remarks>
    /// A store sets it only when every index on the table made it into <see cref="Indexes"/>; one that could
    /// not build the list (its table model failed to load, say) leaves it false rather than report a partial
    /// list as the whole.
    /// </remarks>
    public bool IndexesReported { get; set; }

    /// <summary>
    /// Members duplicated out of the JSON body into their own columns (Marten's duplicated fields,
    /// Polecat's and Fisher's equivalents). A filter on one of these reads a real column rather than the
    /// JSON. Empty either when nothing is duplicated or when the implementation did not populate it —
    /// <see cref="DuplicatedFieldsReported"/> tells the two apart.
    /// </summary>
    public List<DuplicatedFieldDescriptor> DuplicatedFields { get; set; } = new();

    /// <summary>
    /// True when the store populated <see cref="DuplicatedFields"/> completely, so an empty list means no
    /// member is duplicated (jasperfx#1081). False means "not reported", as for <see cref="IndexesReported"/>.
    /// </summary>
    public bool DuplicatedFieldsReported { get; set; }

    /// <summary>
    /// Which metadata columns this type's table captures — per type, where
    /// <see cref="DocumentStoreUsage.DocumentMetadata"/> answers for the whole store (jasperfx#1077). A
    /// document query filtering on a column this type does not capture is refused, so a console reads this
    /// to decide which metadata filters to offer. <see langword="null"/> when the store did not report it.
    /// </summary>
    /// <remarks>
    /// Per type, each flag means "this table has a column for it that a query can filter or order on".
    /// <see cref="DocumentMetadataCapabilities.Version"/> is therefore true only for a real version column
    /// (optimistic concurrency or numeric revisions), even where the store computes a
    /// <see cref="JasperFx.Documents.StoredDocument.Version"/> token for every row; <see cref="DocumentMetadataCapabilities.TenantId"/>
    /// only for a conjoined type; <see cref="DocumentMetadataCapabilities.SoftDelete"/> only for a soft-deleted one.
    /// </remarks>
    public DocumentMetadataCapabilities? Metadata { get; set; }
}

/// <summary>
/// One index on a document table, in structured form. See
/// <see cref="DocumentMappingDescriptor.Indexes"/>.
/// </summary>
public class DocumentIndexDescriptor
{
    /// <summary>The index's name in the database.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The document member paths the index covers, dotted and in declaration casing
    /// (e.g. <c>"ShipTo.City"</c>). Empty for an index over metadata columns only.
    /// </summary>
    public string[] Members { get; set; } = [];

    /// <summary>The table columns or expressions the index is built over, as the store spells them.</summary>
    public string[] Columns { get; set; } = [];

    /// <summary>Whether the index enforces uniqueness.</summary>
    public bool IsUnique { get; set; }

    /// <summary>
    /// The index method where the database has one (<c>"btree"</c>, <c>"gin"</c>, <c>"hnsw"</c>, …), or
    /// null for the database's default.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>The partial-index predicate, or null for a full index.</summary>
    public string? Predicate { get; set; }
}

/// <summary>
/// One document member duplicated into its own column. See
/// <see cref="DocumentMappingDescriptor.DuplicatedFields"/>.
/// </summary>
public class DuplicatedFieldDescriptor
{
    /// <summary>The member path, dotted and in declaration casing (e.g. <c>"ShipTo.City"</c>).</summary>
    public string MemberPath { get; set; } = "";

    /// <summary>The column the value is duplicated into.</summary>
    public string ColumnName { get; set; } = "";

    /// <summary>The column's database type, as the store spells it.</summary>
    public string DbType { get; set; } = "";
}

/// <summary>
/// Structured description of a partitioned document table: the partition
/// strategy (e.g. <c>"List"</c>, <c>"Hash"</c>, <c>"Range"</c>) plus the
/// declared partition names where the store can surface them.
/// </summary>
public class PartitioningDescriptor
{
    /// <summary>
    /// Partition strategy label — <c>"List"</c>, <c>"Hash"</c>, <c>"Range"</c>,
    /// etc. Mirrors the store's partition-strategy kind as a string for
    /// serialization-friendliness across version skews.
    /// </summary>
    public string Strategy { get; set; } = "";

    /// <summary>
    /// Declared partition names where known (e.g. per-tenant or per-range
    /// partition table suffixes). Empty when the partitions are not enumerable.
    /// </summary>
    public string[] PartitionNames { get; set; } = [];
}
