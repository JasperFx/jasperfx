using JasperFx.Core.Reflection;

namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic snapshot of a single EF Core <c>DbContext</c> registered in a
/// host. The EF-Core parallel of <see cref="DocumentStoreUsage"/>: surfaces
/// the operationally-interesting configuration shape — provider, database
/// topology, entity model, pending migrations — for monitoring tools such as
/// CritterWatch (#102).
/// </summary>
/// <remarks>
/// <para>
/// Naming chosen for parallelism with <see cref="DocumentStoreUsage"/> /
/// <c>EventStoreUsage</c> — noun-only, no acronym. The descriptor is
/// EF-Core-agnostic in vocabulary so JasperFx core can carry the type without
/// pulling Microsoft.EntityFrameworkCore in as a dependency. The actual
/// bridge that reads a live <c>DbContext</c> + <c>IModel</c> lives in the EF
/// Core integration package (e.g. <c>Wolverine.EntityFrameworkCore</c>) and
/// populates these first-class fields explicitly inside its
/// <c>TryCreateUsage</c> implementation.
/// </para>
/// <para>
/// Properties living on the base <see cref="OptionsDescription.Properties"/>
/// bag carry settings that are interesting but don't warrant their own
/// first-class slot: command timeout, sensitive-data-logging-on, retry
/// policy, query-tracking behaviour, etc. They're populated by the bridge.
/// </para>
/// </remarks>
public class DbContextUsage : OptionsDescription
{
    public DbContextUsage()
    {
    }

    /// <summary>
    /// Build a usage descriptor for <paramref name="subject"/> identified by
    /// <paramref name="subjectUri"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT chain into <see cref="OptionsDescription(object)"/>:
    /// that ctor invokes the reflective property auto-reader, which on a
    /// concrete <c>DbContext</c> subject walks <c>ChangeTracker</c> /
    /// <c>Database</c> / <c>Model</c> handles and dumps them into
    /// <see cref="OptionsDescription.Children"/> /
    /// <see cref="OptionsDescription.Properties"/>. Those are runtime handles,
    /// not configuration shape; surfacing them in CritterWatch's Storage tab
    /// is noise. The bridge populates the first-class fields explicitly —
    /// <see cref="ContextType"/>, <see cref="ProviderName"/>,
    /// <see cref="Database"/>, <see cref="Entities"/>, etc.
    /// </remarks>
    public DbContextUsage(Uri subjectUri, object subject)
    {
        if (subject == null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        Subject = subject.GetType().FullNameInCode();
        SubjectUri = subjectUri;
        ContextType = TypeDescriptor.For(subject.GetType());
        Version = subject.GetType().Assembly.GetName().Version?.ToString();
    }

    /// <summary>
    /// Version of the assembly that produced this snapshot — typically the
    /// version of the consuming application's DbContext assembly.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Stable URI identifying this DbContext within the host process.
    /// Common shape is <c>efcore://OrdersDbContext</c>; multi-context apps
    /// distinguish by the context type-name in the path. Mirrors
    /// <see cref="DocumentStoreUsage.SubjectUri"/>'s placement.
    /// </summary>
    public Uri SubjectUri { get; set; } = null!;

    /// <summary>
    /// CLR identity of the <c>DbContext</c> subclass — name / fullname /
    /// assembly name. Surfaced as a first-class field (rather than just
    /// living in <see cref="OptionsDescription.Subject"/>) because operators
    /// look it up by short name when a service registers multiple contexts.
    /// </summary>
    public TypeDescriptor ContextType { get; set; } = null!;

    /// <summary>
    /// EF Core provider name from the <c>IRelationalConnection</c> — e.g.
    /// <c>"Microsoft.EntityFrameworkCore.SqlServer"</c>,
    /// <c>"Npgsql.EntityFrameworkCore.PostgreSQL"</c>,
    /// <c>"Microsoft.EntityFrameworkCore.Sqlite"</c>. Empty for in-memory or
    /// when the bridge can't resolve a relational connection.
    /// </summary>
    public string ProviderName { get; set; } = "";

    /// <summary>
    /// Database topology — server / database / cardinality. Same shape used
    /// by <see cref="DocumentStoreUsage.Database"/>. Multi-tenant
    /// EF Core apps populate <see cref="DatabaseUsage.Cardinality"/> and the
    /// per-tenant <see cref="DatabaseUsage.Databases"/> list.
    /// </summary>
    public DatabaseUsage Database { get; set; } = null!;

    /// <summary>
    /// Per-entity mappings — the meat of "what does this DbContext manage".
    /// Each entry mirrors a single EF Core <c>IEntityType</c> (table /
    /// schema / primary key / soft-delete and concurrency hints). Sourced
    /// from <c>DbContext.Model.GetEntityTypes()</c>.
    /// </summary>
    public List<EntityDescriptor> Entities { get; set; } = new();

    /// <summary>
    /// Number of EF Core migrations that have been generated but not yet
    /// applied to <see cref="Database"/>. Populated from
    /// <c>IMigrator.GetPendingMigrationsAsync()</c>; <see langword="null"/>
    /// when the bridge couldn't probe (e.g. the connection wasn't ready, or
    /// the provider doesn't support migrations).
    /// </summary>
    /// <remarks>
    /// First-class because pending-migration count is one of the most-asked
    /// diagnostic questions for EF Core ops. The actual list of migration
    /// names ships through <see cref="OptionsDescription.AddValue"/> as
    /// <c>"PendingMigrations"</c> when the bridge wants to surface them.
    /// </remarks>
    public int? PendingMigrationsCount { get; set; }
}
