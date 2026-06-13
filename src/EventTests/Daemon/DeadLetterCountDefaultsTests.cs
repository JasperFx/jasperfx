using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class DeadLetterCountDefaultsTests
{
    // A bare IEventDatabase that does NOT override the dead-letter count read members,
    // so the calls below exercise the default interface implementations added for
    // jasperfx#356 (the "return 0 / empty" stand-ins for stores that don't yet read
    // dead letters). Everything else throws — those members are never invoked here.
    private sealed class BareEventDatabase : IEventDatabase
    {
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

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private readonly IEventDatabase theDatabase = new BareEventDatabase();

    [Fact]
    public async Task count_dead_letters_default_returns_zero()
    {
        var count = await theDatabase.CountDeadLetterEventsAsync(new ShardName("Fake"));
        count.ShouldBe(0);
    }

    [Fact]
    public async Task fetch_dead_letter_counts_default_returns_empty()
    {
        var counts = await theDatabase.FetchDeadLetterCountsAsync();
        counts.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetch_dead_letter_counts_for_null_tenant_delegates_to_store_global()
    {
        // jasperfx#450: the tenant overload with a null tenant is store-global and reuses today's behavior.
        var counts = await theDatabase.FetchDeadLetterCountsAsync(tenantId: null);
        counts.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetch_dead_letter_counts_for_a_tenant_throws_when_not_partitioned()
    {
        // jasperfx#450: a non-partitioned store has no per-tenant dead-letter dimension, so the default throws.
        await Should.ThrowAsync<NotSupportedException>(() => theDatabase.FetchDeadLetterCountsAsync("tenant-1"));
    }

    [Fact]
    public void dead_letter_shard_count_tenant_id_defaults_to_null()
    {
        // jasperfx#450: null TenantId == store-global / not partitioned, the only behavior before partitioning.
        new DeadLetterShardCount("Orders", "All", 3).TenantId.ShouldBeNull();
    }

    [Fact]
    public void dead_letter_shard_count_carries_tenant_id()
    {
        var count = new DeadLetterShardCount("Orders", "All", 3, "tenant-1");
        count.TenantId.ShouldBe("tenant-1");
    }
}
