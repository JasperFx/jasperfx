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

    public virtual ValueTask DisposeAsync() => theFixture.DisposeAsync();

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

    protected Task<IProjectionDaemon> StartDaemonAsync() => theFixture.StartDaemonAsync();

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
    protected static async Task ShouldFailWithAsync<TException>(Func<Task> action) where TException : Exception
    {
        var exception = await Should.ThrowAsync<Exception>(action).ConfigureAwait(false);

        if (exception is TException)
        {
            return;
        }

        if (exception is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Any(x => x is TException))
        {
            return;
        }

        throw new ShouldAssertException(
            $"Expected {typeof(TException).Name}, directly or aggregated, but got {exception.GetType().FullName}: {exception.Message}");
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
