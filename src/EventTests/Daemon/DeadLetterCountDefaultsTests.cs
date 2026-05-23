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
}
