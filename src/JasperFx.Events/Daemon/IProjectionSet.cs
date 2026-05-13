using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// A group of projection shards that share a single lock — i.e. they're scheduled
/// together as a unit by the projection coordinator. Each set targets exactly one
/// database; multi-database deployments expose one set per database.
/// </summary>
public interface IProjectionSet
{
    /// <summary>
    /// Stable, deterministic identifier used as the underlying lock key when this
    /// set is scheduled. Distributed projection coordinators across nodes must
    /// compute the same id for the same logical set so they negotiate the same lock.
    /// </summary>
    int LockId { get; }

    /// <summary>
    /// The database that this set's shards run against.
    /// </summary>
    IProjectionDatabase Database { get; }

    /// <summary>
    /// The shards in this set.
    /// </summary>
    IReadOnlyList<ShardName> Names { get; }
}
