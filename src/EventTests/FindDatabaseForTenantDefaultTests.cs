using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#1005 — the store-agnostic tenant → database lookup ships as a default interface member, so
/// out-of-tree stores keep compiling. The default answers only the case it can answer safely: a single
/// database holds every tenant. Everything else is null, and the caller falls back to AllDatabases().
/// </summary>
public class FindDatabaseForTenantDefaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Overrides only AllDatabases() and DatabaseCardinality, so the lookup below runs the default.
    private sealed class BareEventStore(DatabaseCardinality cardinality, params IEventDatabase[] databases) : IEventStore
    {
        public ValueTask<IReadOnlyList<IEventDatabase>> AllDatabases()
            => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>(databases);

        public DatabaseCardinality DatabaseCardinality => cardinality;

        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();
        public Uri Subject => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
            string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
            => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
            => throw new NotImplementedException();

        public Meter Meter => throw new NotImplementedException();
        public ActivitySource ActivitySource => throw new NotImplementedException();
        public string MetricsPrefix => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task a_single_database_store_answers_with_its_one_database()
    {
        var database = Substitute.For<IEventDatabase>();
        IEventStore store = new BareEventStore(DatabaseCardinality.Single, database);

        (await store.FindDatabaseForTenantAsync("tenant1", Token)).ShouldBeSameAs(database);
    }

    [Fact]
    public async Task a_store_that_does_not_report_its_databases_answers_null()
    {
        // The AllDatabases() default is empty
        IEventStore store = new BareEventStore(DatabaseCardinality.Single);

        (await store.FindDatabaseForTenantAsync("tenant1", Token)).ShouldBeNull();
    }

    [Theory]
    [InlineData(DatabaseCardinality.StaticMultiple)]
    [InlineData(DatabaseCardinality.DynamicMultiple)]
    [InlineData(DatabaseCardinality.None)]
    public async Task a_multi_database_store_that_has_not_overridden_it_answers_null(DatabaseCardinality cardinality)
    {
        IEventStore store = new BareEventStore(cardinality, Substitute.For<IEventDatabase>(), Substitute.For<IEventDatabase>());

        (await store.FindDatabaseForTenantAsync("tenant1", Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_null_tenant_id_is_refused()
    {
        IEventStore store = new BareEventStore(DatabaseCardinality.Single, Substitute.For<IEventDatabase>());

        await Should.ThrowAsync<ArgumentNullException>(async () => await store.FindDatabaseForTenantAsync(null!, Token));
    }
}
