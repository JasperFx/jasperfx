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
}
