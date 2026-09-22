using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using JasperFx.Events.Tags;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Base class for every shared compliance suite. Owns the fixture lifecycle and forwards the seam
/// members so the [Fact] bodies read like ordinary event store tests.
/// </summary>
/// <typeparam name="TFixture">The store's concrete fixture.</typeparam>
/// <typeparam name="TOperations">The store's writable session type.</typeparam>
/// <typeparam name="TQuerySession">The store's read-only session type.</typeparam>
/// <remarks>
/// A consuming store enrolls a suite with a single empty subclass closing the three generics.
/// </remarks>
public abstract class EventStoreComplianceSuite<TFixture, TOperations, TQuerySession> : IAsyncLifetime
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    protected readonly TFixture theFixture = new();

    /// <summary>
    /// The suite's standard store configuration. Implementations MUST return the same delegate
    /// instance every time (hold it in a static field) so the fixture can skip redundant rebuilds.
    /// </summary>
    protected abstract Action<ComplianceStoreConfig> Configuration { get; }

    public virtual async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync().ConfigureAwait(false);
        await theFixture.ConfigureAsync(Configuration).ConfigureAwait(false);
        await theFixture.CleanEventDataAsync().ConfigureAwait(false);
    }

    public virtual async ValueTask DisposeAsync()
    {
        // ⚠️ Before the fixture, and this ordering is the fix for jasperfx#790. xUnit builds a new
        // test-class instance per fact, so this runs at the END OF EVERY FACT -- but the STORE the
        // daemon runs against is cached across facts (keyed on the configuration delegate's
        // identity, which is why implementations must return the same instance). A daemon left
        // running therefore outlives the fact that started it and goes on delivering into the next
        // one, against the single subscription instance the suite shares.
        await StopDaemonAsync().ConfigureAwait(false);

        await theFixture.DisposeAsync().ConfigureAwait(false);
    }

    protected CancellationToken Cancellation => theFixture.Cancellation;

    protected TOperations OpenSession() => theFixture.OpenSession();

    protected IEventStoreOperations EventsFor(TOperations session) => theFixture.EventsFor(session);

    protected string? CorrelationIdFor(TOperations session) => theFixture.CorrelationIdFor(session);

    protected string? CausationIdFor(TOperations session) => theFixture.CausationIdFor(session);

    protected void SetCorrelationId(TOperations session, string? correlationId)
        => theFixture.SetCorrelationId(session, correlationId);

    protected void SetUserName(TOperations session, string? userName)
        => theFixture.SetUserName(session, userName);

    protected Task SaveChangesAsync(TOperations session) => theFixture.SaveChangesAsync(session, Cancellation);

    protected Task<T?> LoadDocumentAsync<T>(TQuerySession session, object id) where T : class
        => theFixture.LoadDocumentAsync<T>(session, id, Cancellation);

    protected void StoreDocument<T>(TOperations session, T document) where T : notnull
        => theFixture.StoreDocument(session, document);

    protected IEventStore EventStore => theFixture.EventStore;

    protected IComplianceBatch CreateBatch(TQuerySession session) => theFixture.CreateBatch(session);

    /// <summary>
    /// The event type name the store persists for <typeparamref name="T"/>, resolved through the
    /// shared registry surface (never a store-internal generic).
    /// </summary>
    protected string EventTypeNameFor<T>() => theFixture.Registry.EventMappingFor(typeof(T)).EventTypeName;

    private IProjectionDaemon? _daemon;

    /// <summary>
    /// Start the async daemon for this fact, stopping whatever the previous fact started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Stopping the previous one is the point, not tidiness</b> (jasperfx#790). Facts used to
    /// start daemons and walk away, leaving the fixture to dispose them at CLASS teardown — so a
    /// daemon from an earlier fact went on delivering and committing while a later fact ran, against
    /// the one subscription instance the whole suite shares.
    /// </para>
    /// <para>
    /// That produced a failure that looked like a store bug and was not:
    /// <c>SubscriptionCompliance.the_listener_returned_by_the_subscription_runs_after_the_commit</c>
    /// failing with <c>VisibleAtCommit should be True but was null</c>, roughly one run in a
    /// thousand tests and never in isolation. A stale daemon's commit both consumed the
    /// first-call-only probe slot and satisfied the fact's wait, so the fact asserted without its own
    /// probe ever running. <c>NaturalKeyCompliance</c> has the same hazard by construction — three
    /// facts start a daemon and immediately rebuild the very projection whose shard is running.
    /// </para>
    /// <para>
    /// A store whose <c>StopAllAsync</c> throws during teardown is swallowed deliberately: this is
    /// test hygiene between facts, and a failure here would mask the assertion the fact was making.
    /// </para>
    /// </remarks>
    protected async Task<IProjectionDaemon> StartDaemonAsync()
    {
        await StopDaemonAsync().ConfigureAwait(false);

        _daemon = await theFixture.StartDaemonAsync().ConfigureAwait(false);
        return _daemon;
    }

    /// <summary>
    /// Stop the daemon this suite started, if any. Safe to call when none was started.
    /// </summary>
    protected async Task StopDaemonAsync()
    {
        var running = _daemon;
        _daemon = null;

        if (running is null) return;

        try
        {
            await running.StopAllAsync().ConfigureAwait(false);
        }
        catch
        {
            // See the remarks on StartDaemonAsync: teardown noise must not mask a fact's assertion.
        }
    }

    /// <summary>
    /// Build and start an application host with the store registered the documented way plus its
    /// documented async daemon registration, for the projection coordinator suite. See
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.StartCoordinatorHostAsync(bool)"/>.
    /// </summary>
    protected Task<IComplianceCoordinatorHost<TOperations>> StartCoordinatorHostAsync(
        bool includeAncillaryStore = false)
        => theFixture.StartCoordinatorHostAsync(includeAncillaryStore);

    protected Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
        => theFixture.WaitForNonStaleProjectionDataAsync(timeout);

    /// <summary>
    /// Every row of a flat-table projection's table. See
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.QueryTableAsync"/> for why
    /// this is deliberately predicate-free.
    /// </summary>
    protected Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryTableAsync(string tableName)
        => theFixture.QueryTableAsync(tableName, Cancellation);

    /// <summary>
    /// Assert that an operation fails with <typeparamref name="TException"/>, whether the store
    /// throws it directly or wrapped in an <see cref="AggregateException"/>.
    /// </summary>
    /// <remarks>
    /// A real cross-store divergence rather than test convenience: Marten surfaces
    /// DcbConcurrencyException straight out of SaveChangesAsync, while Polecat runs its unit of work
    /// in parallel and aggregates. The failure semantics are identical, so the suites assert the
    /// semantics and let the exception shape vary.
    /// </remarks>
    protected static Task ShouldFailWithAsync<TException>(Func<Task> action) where TException : Exception
        => ShouldFailWithAsync(typeof(TException), action);

    /// <summary>
    /// Assert that an operation fails with the exception type this store nominates for
    /// <paramref name="kind"/>, whether thrown directly or wrapped in an
    /// <see cref="AggregateException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used where the failure <em>category</em> is shared across every store but the exception
    /// <em>type</em> is not. The event store exceptions lifted into <c>JasperFx.Events</c> are
    /// the default answer, so a store that adopted them names nothing; a store that owns its own
    /// hierarchy overrides
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.ExceptionTypeFor"/>.
    /// </para>
    /// <para>
    /// Marten is the concrete reason this is variable: its
    /// <c>all_exceptions_should_derive_from_MartenException</c> convention test forces every Marten
    /// exception onto <c>MartenException</c>, and single inheritance means a Marten type cannot
    /// also derive from the lifted JasperFx type. Owning your exception hierarchy is a legitimate
    /// store decision, so the suite asserts the behaviour and lets the store name the type.
    /// </para>
    /// <para>
    /// This is not a weakening. An exception of the nominated type is still required — "something
    /// threw" would not pass. Do not replace a <see cref="ComplianceExceptionKind"/> assertion with
    /// a hard type; that re-imposes a shared hierarchy on stores that have declined it.
    /// </para>
    /// </remarks>
    protected Task ShouldFailWithAsync(ComplianceExceptionKind kind, Func<Task> action)
        => ShouldFailWithAsync(theFixture.ExceptionTypeFor(kind), action);

    private static async Task ShouldFailWithAsync(Type expectedType, Func<Task> action)
    {
        var exception = await Should.ThrowAsync<Exception>(action).ConfigureAwait(false);

        if (expectedType.IsInstanceOfType(exception))
        {
            return;
        }

        if (exception is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Any(expectedType.IsInstanceOfType))
        {
            return;
        }

        throw new ShouldAssertException(
            $"Expected {expectedType.Name}, directly or aggregated, but got {exception.GetType().FullName}: {exception.Message}");
    }

    /// <summary>
    /// Convenience for the DCB suites: build an event envelope, stamp tags on it, append it and save.
    /// </summary>
    protected async Task AppendTaggedEventAsync(TOperations session, Guid streamId, object eventData,
        params object[] tags)
    {
        var events = EventsFor(session);
        var wrapped = events.BuildEvent(eventData);
        wrapped.WithTag(tags);
        events.Append(streamId, wrapped);
        await SaveChangesAsync(session).ConfigureAwait(false);
    }
}
