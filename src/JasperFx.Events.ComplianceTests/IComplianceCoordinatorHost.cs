using System;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// A started application host wrapping a store registered the documented way — the product's
/// <c>AddXxx(...)</c> service registration plus its documented async daemon registration — handed
/// to <see cref="ProjectionCoordinatorCompliance{TFixture,TOperations,TQuerySession}"/> by
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.StartCoordinatorHostAsync()"/>.
/// </summary>
/// <typeparam name="TOperations">The store's writable session type.</typeparam>
/// <remarks>
/// <para>
/// Deliberately tiny. The suite resolves everything else through <see cref="Services"/> and the
/// shared interfaces (<c>IProjectionCoordinator</c>, <c>IHostedService</c>,
/// <c>IProjectionDaemon</c>); the one thing a generic suite cannot pull out of a container
/// portably is a writable session typed as the fixture's session pair, which is what
/// <see cref="OpenSession"/> supplies. Every other seam member the suite already has —
/// <c>SaveChangesAsync</c>, <c>LoadDocumentAsync</c>, <c>EventsFor</c> — takes the session as a
/// parameter, so it works against a hosted store's session unchanged.
/// </para>
/// <para>
/// <see cref="IAsyncDisposable.DisposeAsync"/> must STOP the host gracefully before disposing it —
/// Microsoft's <c>IHost.Dispose</c> alone does not call <c>StopAsync</c>, and an abandoned daemon
/// host leaks agents into the next test.
/// </para>
/// </remarks>
public interface IComplianceCoordinatorHost<out TOperations> : IAsyncDisposable
{
    /// <summary>
    /// The started host's root service provider — the container an application would resolve
    /// <c>IProjectionCoordinator</c> from.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Open a writable session against the hosted store — not against the fixture's own store
    /// instance. Callers dispose it.
    /// </summary>
    TOperations OpenSession();

    /// <summary>
    /// The hosted store as <see cref="IEventStore" /> — the same store
    /// <see cref="Services" /> resolved the coordinator for, not the fixture's own instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added for <c>ProjectionStatusCompliance</c> (jasperfx#818), which needs the one store in the
    /// suite set that has a genuinely reachable running daemon. Every other daemon suite drives a
    /// daemon the fixture built by hand, and a hand-built daemon is exactly the case
    /// <see cref="JasperFx.Descriptors.ShardStatusState.Unknown" /> describes — so asking the
    /// fixture's own store what state its shards are in can only ever answer "no daemon here to
    /// ask", and the daemon-visible half of the contract would be untestable.
    /// </para>
    /// <para>
    /// A default-throwing member rather than an added abstract one, matching
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.StartCoordinatorHostAsync(bool)" />:
    /// consumers keep compiling across the version bump, and a store that has not implemented it
    /// fails the one fact that reads it rather than skipping silently. It cannot be resolved from
    /// <see cref="Services" /> by a generic suite — no store registers itself as
    /// <see cref="IEventStore" />; each registers its own <c>IDocumentStore</c>.
    /// </para>
    /// </remarks>
    IEventStore EventStore
        => throw new NotSupportedException(
            $"{GetType().FullName} does not expose the hosted store as IEventStore, so it cannot run the daemon-visible facts of ProjectionStatusCompliance (jasperfx#818).");
}
