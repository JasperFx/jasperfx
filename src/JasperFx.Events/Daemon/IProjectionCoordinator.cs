using Microsoft.Extensions.Hosting;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Coordinates the projection daemons that run for an event store across one or more
/// databases. Hosted as an <see cref="IHostedService"/> so the runtime can start and
/// stop the daemons with the application lifetime, and pause/resume them at runtime
/// for diagnostics or maintenance.
/// </summary>
/// <remarks>
/// Lifted from Marten's <c>Marten.Events.Daemon.Coordination.IProjectionCoordinator</c>
/// as the canonical home. Concrete implementations remain product-specific so they can
/// integrate with each event store's database resolution + tenancy model.
/// </remarks>
public interface IProjectionCoordinator : IHostedService
{
    /// <summary>
    /// Returns the daemon running against the default/main database for this event store.
    /// </summary>
    IProjectionDaemon DaemonForMainDatabase();

    /// <summary>
    /// Returns the daemon running against the database with the given identifier.
    /// Resolves the database lazily — implementations may need to open a connection
    /// to discover databases that have not yet been observed.
    /// </summary>
    ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier);

    /// <summary>
    /// All daemons currently running across all databases known to this coordinator.
    /// </summary>
    ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync();

    /// <summary>
    /// Stops the coordinator's automatic restart logic and stops all running agents
    /// across all daemons. Does not release any held locks. Use <see cref="ResumeAsync"/>
    /// to restart.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resumes the coordinator's automatic restart logic and restarts all agents across
    /// all daemons. Intended to be paired with a prior <see cref="PauseAsync"/> call.
    /// </summary>
    Task ResumeAsync();
}

/// <summary>
/// Marker variant of <see cref="IProjectionCoordinator"/> used to register multiple
/// coordinators in DI when an application talks to more than one event store. The
/// type parameter acts as a unique key for ancillary stores; it has no behavioral
/// constraint of its own.
/// </summary>
/// <typeparam name="T">A marker type identifying which event store this coordinator manages.</typeparam>
public interface IProjectionCoordinator<T> : IProjectionCoordinator where T : class
{
}
