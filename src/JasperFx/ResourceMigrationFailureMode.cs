namespace JasperFx;

/// <summary>
/// Controls what happens when a resource (database schema / migration, message broker objects, etc.)
/// fails to set up or migrate during application startup via the resource setup hosted service.
/// Configured per <see cref="Profile"/> so it can differ between development and production.
/// </summary>
public enum ResourceMigrationFailureMode
{
    /// <summary>
    /// A resource setup or migration failure at startup aborts application startup by throwing.
    /// This is the default and is usually the right choice for development (fail fast).
    /// </summary>
    FailFast,

    /// <summary>
    /// Resource setup or migration failures at startup are logged but do NOT prevent the application
    /// from starting. Useful in production multi-replica deployments where, for example, a replica that
    /// loses the migration lock during a rolling deploy would otherwise crash-loop even though the
    /// winning replica's committed migration makes the schema current. The application starts and
    /// operates against whatever state is current.
    /// </summary>
    ContinueOnFailures
}
