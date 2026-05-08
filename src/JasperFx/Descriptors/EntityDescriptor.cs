namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic mirror of a single EF Core entity type as configured on a
/// <c>DbContext</c>. Parallel of <see cref="DocumentMappingDescriptor"/> for
/// the EF Core integration: monitoring tools (CritterWatch) surface the
/// per-entity table / schema / key shape so operators can see what each
/// context is mapped against without cracking open the source.
/// </summary>
/// <remarks>
/// Stays EF-Core-agnostic in vocabulary so JasperFx core can carry the type
/// without taking a dependency on Microsoft.EntityFrameworkCore. The bridge
/// in the integration package (e.g. <c>Wolverine.EntityFrameworkCore</c>)
/// reads the live <c>IModel</c> and populates these fields explicitly.
/// </remarks>
public class EntityDescriptor
{
    /// <summary>
    /// CLR identity of the entity type — name / fullname / assembly name.
    /// </summary>
    public TypeDescriptor EntityType { get; set; } = null!;

    /// <summary>
    /// Resolved schema name where the entity's table lives, or empty when
    /// the provider doesn't surface a schema (SQLite, in-memory).
    /// </summary>
    public string Schema { get; set; } = "";

    /// <summary>
    /// Resolved table name. EF Core's <c>StoreObjectIdentifier</c> resolves
    /// this from <c>IEntityType.GetTableName()</c>; falls back to the entity
    /// type's name when the model didn't override it.
    /// </summary>
    public string TableName { get; set; } = "";

    /// <summary>
    /// Column name(s) of the primary key — joined by <c>", "</c> when the
    /// key is composite. Empty when the entity is keyless (a query type or
    /// a view-mapped entity).
    /// </summary>
    public string PrimaryKey { get; set; } = "";

    /// <summary>
    /// Whether EF Core's global query filter on this entity excludes
    /// soft-deleted rows. <see langword="true"/> when the entity has any
    /// query filter configured (the bridge can't always tell whether the
    /// filter is a soft-delete predicate vs. a tenant predicate vs. some
    /// other domain rule, but the flag at least flags "filtered").
    /// </summary>
    public bool HasQueryFilter { get; set; }

    /// <summary>
    /// Name of the concurrency-token column when the entity has one,
    /// otherwise empty. Surfaced because operators investigating
    /// concurrency-conflict spikes want to see the configured token at a
    /// glance.
    /// </summary>
    public string ConcurrencyToken { get; set; } = "";

    /// <summary>
    /// Discriminator column for inheritance-mapped entities (TPH /
    /// table-per-hierarchy) when present. Empty for plain entities.
    /// </summary>
    public string Discriminator { get; set; } = "";

    /// <summary>
    /// Whether the entity is mapped to a database view rather than a table.
    /// EF Core surfaces this through <c>IEntityType.GetViewName()</c>.
    /// </summary>
    public bool IsView { get; set; }

    /// <summary>
    /// <see langword="true"/> when the CLR entity type derives from
    /// Wolverine's <c>Saga</c> base class. Surfaced per-entity so the
    /// Storage tab can render saga rows with a chip linking out to the
    /// dedicated Sagas tab without having to cross-reference
    /// <see cref="DbContextUsage.SagaTypes"/>.
    /// </summary>
    public bool IsSaga { get; set; }

    /// <summary>
    /// <see langword="true"/> for EF Core "owned" entity types — types whose
    /// table is logically a child of another entity's table and whose
    /// lifetime is bound to the owner's. Operators staring at a long entity
    /// list use this to fold owned types into their owner.
    /// </summary>
    public bool IsOwned { get; set; }

    /// <summary>
    /// All non-primary-key indexes EF Core has configured on this entity —
    /// name, uniqueness, and the columns that make up the index. Matches
    /// the per-document index list on <see cref="DocumentMappingDescriptor"/>
    /// in operator weight: usually the first thing checked when investigating
    /// a slow-query report.
    /// </summary>
    public List<IndexDescriptor> Indexes { get; set; } = new();

    /// <summary>
    /// All foreign-key relationships EF Core has configured on this entity.
    /// Self-referencing FKs are filtered out by the bridge — they're noise
    /// in the operator view and can be inferred from the entity name. The
    /// bridge captures only the configuration shape (name + target table +
    /// key columns); navigation properties don't ship through this snapshot.
    /// </summary>
    public List<ForeignKeyDescriptor> ForeignKeys { get; set; } = new();

    /// <summary>
    /// Resolved view name when <see cref="IsView"/> is <see langword="true"/>,
    /// otherwise <see langword="null"/>. Sourced from
    /// <c>IEntityType.GetViewName()</c>; lets operators distinguish the
    /// underlying view from the entity name when those don't agree.
    /// </summary>
    public string? ViewName { get; set; }
}

/// <summary>
/// Diagnostic mirror of a single EF Core index — name, uniqueness, and
/// participating column names. Surfaced inside
/// <see cref="EntityDescriptor.Indexes"/> so monitoring tools can render
/// the per-entity index list without cracking open <c>IEntityType</c>.
/// </summary>
public class IndexDescriptor
{
    /// <summary>
    /// Index name as configured on the model (or the EF Core convention-
    /// generated name when the application didn't override it).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// <see langword="true"/> when the index enforces a uniqueness constraint
    /// — operators investigating duplicate-key write failures look here
    /// first.
    /// </summary>
    public bool IsUnique { get; set; }

    /// <summary>
    /// Ordered list of column names participating in the index. Matches the
    /// order EF Core configured them in — composite-index column order is
    /// load-bearing for query planning, so the descriptor preserves it.
    /// </summary>
    public List<string> Columns { get; set; } = new();
}

/// <summary>
/// Diagnostic mirror of a single EF Core foreign-key relationship — name,
/// target table, and the columns on the source entity that carry the FK.
/// Used inside <see cref="EntityDescriptor.ForeignKeys"/>.
/// </summary>
/// <remarks>
/// Deliberately minimal: the bridge does NOT surface the navigation
/// property names, on-delete behaviour, or the principal-key columns. The
/// goal is to give operators a quick "this entity references that table"
/// view, not to recreate <c>dotnet ef migrations script</c>.
/// </remarks>
public class ForeignKeyDescriptor
{
    /// <summary>
    /// Foreign-key constraint name as configured on the model (or the
    /// convention-generated name when the application didn't override it).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Database table the foreign key points at — schema-qualified when
    /// the target lives in a non-default schema, bare table name otherwise.
    /// </summary>
    public string TargetTable { get; set; } = "";

    /// <summary>
    /// Ordered column names on this entity's table that hold the foreign-key
    /// values. Order matches the principal-key column order on the target.
    /// </summary>
    public List<string> KeyColumns { get; set; } = new();
}
