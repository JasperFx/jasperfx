using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

public class DeleteProjectionProgressByShardNameDefaultsTests
{
    // A bare IEventDatabase that does NOT override DeleteProjectionProgressByShardNameAsync,
    // so the call exercises the default interface implementation added for jasperfx#473.
    // Stores that don't support raw shard-identity progression deletes must signal that
    // loudly rather than silently no-op, so the default throws NotSupportedException.
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

    // An IEventDatabase that DOES override the new member, capturing the raw identity it was
    // handed. Proves the abstraction targets the exact shard identity store-agnostically and
    // that an implementer's override is honored over the throwing default.
    private sealed class CapturingEventDatabase : IEventDatabase
    {
        public string? DeletedIdentity { get; private set; }

        public Task DeleteProjectionProgressByShardNameAsync(string shardIdentity, CancellationToken token = default)
        {
            DeletedIdentity = shardIdentity;
            return Task.CompletedTask;
        }

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

    [Fact]
    public async Task default_throws_not_supported_so_stores_must_opt_in()
    {
        IEventDatabase database = new BareEventDatabase();
        await Should.ThrowAsync<NotSupportedException>(
            () => database.DeleteProjectionProgressByShardNameAsync("claim_lines:V9:All"));
    }

    [Fact]
    public async Task override_receives_the_raw_shard_identity_verbatim()
    {
        var database = new CapturingEventDatabase();
        await database.DeleteProjectionProgressByShardNameAsync("claim_lines:V9:All");
        database.DeletedIdentity.ShouldBe("claim_lines:V9:All");
    }
}
