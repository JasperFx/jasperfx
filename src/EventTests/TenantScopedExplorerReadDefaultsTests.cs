using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#503 — the tenant-scoped explorer read overloads ship as default interface
/// implementations so out-of-tree stores keep compiling. These pin the two halves of the
/// graceful-degradation contract: a null tenant is store-global and delegates to the
/// tenant-less member, and a non-null tenant throws rather than silently serving a read
/// from whichever tenant the store's default session happened to resolve.
/// </summary>
public class TenantScopedExplorerReadDefaultsTests
{
    // Overrides only the tenant-LESS explorer reads, recording that they were reached. That is
    // what lets these tests prove the null-tenant overload actually delegates, rather than merely
    // failing to throw. The tenant-scoped overloads are deliberately left un-overridden — they are
    // the defaults under test.
    private sealed class ExplorerEventStore : IEventStore
    {
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(int count, CancellationToken ct)
        {
            Calls.Add($"GetRecentStreamsAsync({count})");
            return Task.FromResult<IReadOnlyList<StreamSummary>>([]);
        }

        public async IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, CancellationToken ct)
        {
            Calls.Add($"ReadStreamAsync({streamId})");
            await Task.CompletedTask;
            yield break;
        }

        public Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, CancellationToken ct)
        {
            Calls.Add($"GetStreamMetadataAsync({streamId})");
            return Task.FromResult<StreamMetadata?>(null);
        }

        public async IAsyncEnumerable<EventRecord> QueryByTagsAsync(
            IReadOnlyDictionary<string, string> tags, CancellationToken ct)
        {
            Calls.Add($"QueryByTagsAsync({tags.Count} tags)");
            await Task.CompletedTask;
            yield break;
        }

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

    private readonly ExplorerEventStore theRecorder = new();

    // The tenant overloads are default interface members, so they are only reachable through the
    // interface — not off the concrete type. That is exactly how CritterWatch consumes them.
    private IEventStore theStore => theRecorder;

    [Fact]
    public async Task get_recent_streams_for_null_tenant_delegates_to_store_global()
    {
        await theStore.GetRecentStreamsAsync(5, tenantId: null, CancellationToken.None);
        theRecorder.Calls.ShouldBe(["GetRecentStreamsAsync(5)"]);
    }

    [Fact]
    public async Task get_recent_streams_for_a_tenant_throws_when_not_multi_tenanted()
    {
        await Should.ThrowAsync<NotSupportedException>(
            () => theStore.GetRecentStreamsAsync(5, "tenant-1", CancellationToken.None));
        theRecorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task read_stream_for_null_tenant_delegates_to_store_global()
    {
        await foreach (var _ in theStore.ReadStreamAsync("stream-1", tenantId: null, CancellationToken.None))
        {
        }

        theRecorder.Calls.ShouldBe(["ReadStreamAsync(stream-1)"]);
    }

    [Fact]
    public void read_stream_for_a_tenant_throws_when_not_multi_tenanted()
    {
        // The default is an expression-bodied member rather than an async iterator, so it throws on
        // the call itself instead of deferring to the first MoveNextAsync. Asserted that way on
        // purpose: a caller that never enumerates still gets told the tenant scope was ignored.
        Should.Throw<NotSupportedException>(
            () => theStore.ReadStreamAsync("stream-1", "tenant-1", CancellationToken.None));
        theRecorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task get_stream_metadata_for_null_tenant_delegates_to_store_global()
    {
        await theStore.GetStreamMetadataAsync("stream-1", tenantId: null, CancellationToken.None);
        theRecorder.Calls.ShouldBe(["GetStreamMetadataAsync(stream-1)"]);
    }

    [Fact]
    public async Task get_stream_metadata_for_a_tenant_throws_when_not_multi_tenanted()
    {
        await Should.ThrowAsync<NotSupportedException>(
            () => theStore.GetStreamMetadataAsync("stream-1", "tenant-1", CancellationToken.None));
        theRecorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task query_by_tags_for_null_tenant_delegates_to_store_global()
    {
        var tags = new Dictionary<string, string> { ["account"] = "abc" };
        await foreach (var _ in theStore.QueryByTagsAsync(tags, tenantId: null, CancellationToken.None))
        {
        }

        theRecorder.Calls.ShouldBe(["QueryByTagsAsync(1 tags)"]);
    }

    [Fact]
    public void query_by_tags_for_a_tenant_throws_when_not_multi_tenanted()
    {
        // Same shape as ReadStreamAsync: the default is an expression-bodied member, so it throws on the
        // call itself rather than deferring to the first MoveNextAsync. A caller that never enumerates
        // still gets told the tenant scope was ignored.
        var tags = new Dictionary<string, string> { ["account"] = "abc" };
        Should.Throw<NotSupportedException>(
            () => theStore.QueryByTagsAsync(tags, "tenant-1", CancellationToken.None));
        theRecorder.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void event_query_tenant_id_defaults_to_store_global()
    {
        // The Event Explorer's metadata/event query carries its tenant scope on EventQuery itself
        // (jasperfx#555). Default null preserves today's store-global behavior for every existing caller.
        new EventQuery().TenantId.ShouldBeNull();
        new EventQuery { TenantId = "tenant-1" }.TenantId.ShouldBe("tenant-1");
    }

    // A bare IEventDatabase whose only real member is the tenant-less head sequence, so the tenant
    // overload's delegation is observable through the returned value.
    private sealed class HeadSequenceEventDatabase : IEventDatabase
    {
        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token) => Task.FromResult(42L);

        public string Identifier => throw new NotImplementedException();
        public Uri DatabaseUri => throw new NotImplementedException();
        public ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
            => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private readonly IEventDatabase theDatabase = new HeadSequenceEventDatabase();

    [Fact]
    public async Task fetch_highest_event_sequence_for_null_tenant_delegates_to_store_global()
    {
        var sequence = await theDatabase.FetchHighestEventSequenceNumber(tenantId: null, CancellationToken.None);
        sequence.ShouldBe(42L);
    }

    [Fact]
    public async Task fetch_highest_event_sequence_for_a_tenant_throws_when_not_partitioned()
    {
        // Under UseTenantPartitionedEvents each tenant draws its own sequence, so quietly handing back
        // the store-global head would render a plausible-looking but wrong lag number.
        await Should.ThrowAsync<NotSupportedException>(
            () => theDatabase.FetchHighestEventSequenceNumber("tenant-1", CancellationToken.None));
    }
}
