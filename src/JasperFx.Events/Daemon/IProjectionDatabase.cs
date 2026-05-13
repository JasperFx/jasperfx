namespace JasperFx.Events.Daemon;

/// <summary>
/// Minimal database identity contract consumed by projection-coordination plumbing.
/// Lets shared coordinator code refer to a database by identifier and URI without
/// pulling in Marten's <c>IMartenDatabase</c> or Polecat's session/database types.
/// </summary>
/// <remarks>
/// Concrete event-store implementations satisfy this contract by wrapping their own
/// database abstraction. The coordinator only ever needs the identifier (for shard
/// addressing) and a URI for telemetry.
/// </remarks>
public interface IProjectionDatabase
{
    /// <summary>
    /// Stable identifier for this database. In multi-tenant or multi-database setups
    /// the coordinator uses this string to address a specific daemon.
    /// </summary>
    string Identifier { get; }

    /// <summary>
    /// A URI describing this database, primarily for telemetry / diagnostics output.
    /// </summary>
    Uri DatabaseUri { get; }
}
