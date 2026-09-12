using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTests.Projections;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#815 — <c>AllShards()</c> is declared only on <c>IEventStore&lt;TOperations, TQuerySession&gt;</c>,
/// so a consumer that ships one assembly against Marten, Polecat and Fisher alike cannot ask what is
/// registered. <c>RegisteredShardNames()</c> is the non-generic way in. These pin both halves: the generic
/// interface answers it from its own registry with no change to any store, and a store that implements only
/// the non-generic interface says so rather than claiming an empty registry.
/// </summary>
public class RegisteredShardNamesTests
{
    private static AsyncShard<FakeOperations, FakeSession> shard(string projectionName, string? tenantId = null)
        => new(new AsyncOptions(), ShardRole.Projection,
            new ShardName(projectionName, ShardName.All, 1, tenantId), null!, new EventFilterable());

    // Implements the GENERIC interface and nothing else — RegisteredShardNames() is deliberately not
    // written here, because the point of the test is that the generic interface already satisfies it.
    private sealed class ShardRegistryEventStore(params AsyncShard<FakeOperations, FakeSession>[] shards)
        : IEventStore<FakeOperations, FakeSession>
    {
        public IReadOnlyList<AsyncShard<FakeOperations, FakeSession>> AllShards() => shards;

        public IEventRegistry Registry => throw new NotImplementedException();
        public Type IdentityTypeForProjectedType(Type aggregateType) => throw new NotImplementedException();
        public string DefaultDatabaseName => throw new NotImplementedException();
        public ErrorHandlingOptions ContinuousErrors => throw new NotImplementedException();
        public ErrorHandlingOptions RebuildErrors => throw new NotImplementedException();
        public TimeProvider TimeProvider => throw new NotImplementedException();
        public AutoCreate AutoCreateSchemaObjects => throw new NotImplementedException();

        public Task RewindSubscriptionProgressAsync(IEventDatabase database, string subscriptionName,
            CancellationToken token, long? sequenceFloor) => throw new NotImplementedException();

        public Task RewindAgentProgressAsync(IEventDatabase database, string shardName, CancellationToken token,
            long sequenceFloor) => throw new NotImplementedException();

        public Task TeardownExistingProjectionStateAsync(IEventDatabase database, string subscriptionName,
            CancellationToken token) => throw new NotImplementedException();

        public Task DeleteProjectionProgressAsync(IEventDatabase database, string subscriptionName,
            CancellationToken token) => throw new NotImplementedException();

        public ValueTask<IProjectionBatch<FakeOperations, FakeSession>> StartProjectionBatchAsync(EventRange range,
            IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions,
            CancellationToken token) => throw new NotImplementedException();

        public IEventLoader BuildEventLoader(IEventDatabase database, ILogger loggerFactory,
            EventFilterable filtering, AsyncOptions shardOptions) => throw new NotImplementedException();

        public FakeOperations OpenSession(IEventDatabase database) => throw new NotImplementedException();

        public FakeOperations OpenSession(IEventDatabase database, string tenantId)
            => throw new NotImplementedException();

        public ErrorHandlingOptions ErrorHandlingOptions(ShardExecutionMode mode)
            => throw new NotImplementedException();

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
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    // Implements ONLY the non-generic interface, so RegisteredShardNames() falls to the base default.
    private sealed class BareEventStore : IEventStore
    {
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
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public void the_generic_interface_answers_from_its_own_registry()
    {
        // Held as the NON-generic interface on purpose: that is the only handle a store-agnostic
        // consumer like Wolverine.CritterWatch has, and the whole point of the issue.
        IEventStore theStore = new ShardRegistryEventStore(
            shard("WidgetTally"), shard("VoyageLog"), shard("WidgetTally", "tenant-a"));

        theStore.RegisteredShardNames().Select(x => x.Identity)
            .ShouldBe(["WidgetTally:All", "VoyageLog:All", "WidgetTally:All:tenant-a"]);
    }

    [Fact]
    public void a_tenant_qualified_shard_keeps_its_tenant_through_the_non_generic_read()
    {
        // FetchProjectionLagAsync correlates on the shard NAME, so a tenant-qualified registration has
        // to survive the trip through the non-generic surface or the correlation silently loses a cell.
        IEventStore theStore = new ShardRegistryEventStore(
            shard("WidgetTally"), shard("VoyageLog"), shard("WidgetTally", "tenant-a"));

        theStore.RegisteredShardNames().ShouldContain(x => x.TenantId == "tenant-a");
    }

    [Fact]
    public void an_empty_registry_answers_empty_rather_than_throwing()
    {
        IEventStore theStore = new ShardRegistryEventStore();

        theStore.RegisteredShardNames().ShouldBeEmpty();
    }

    [Fact]
    public void the_non_generic_default_refuses_rather_than_claiming_an_empty_registry()
    {
        // Returning [] here would be indistinguishable from "nothing is registered", which is exactly
        // the answer that latches a readiness probe green during a version bump.
        IEventStore theStore = new BareEventStore();

        Should.Throw<NotSupportedException>(() => theStore.RegisteredShardNames());
    }
}
