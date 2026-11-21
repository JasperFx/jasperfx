using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace InMemoryEventStoreTests;

/// <summary>
/// jasperfx#1255: a running daemon parks, replays and discards, and every change of the shard's parked count reaches
/// the extended progression write with the periodic heartbeat off (the default). The in-memory store keeps no
/// extended progression of its own, so a recording database stands in for the store's write.
/// </summary>
public class parked_count_reaches_extended_progression : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly InMemoryDocumentStore theStore = new();
    private readonly RecordingDatabase theWrites = new();
    private readonly List<Guid> thePoisoned = new();
    private InMemoryEventDatabase theDatabase = null!;
    private ExtendedProgressionWriter theWriter = null!;
    private IDisposable theSubscription = null!;
    private IProjectionDaemon theDaemon = null!;
    private string theProjectionName = null!;

    public async ValueTask InitializeAsync()
    {
        theStore.Events.AddEventType(typeof(Counted));
        theStore.Events.AddEventType(typeof(CountedPoison));
        theStore.Projections.Snapshot<ParkTally>(SnapshotLifecycle.Async);
        theStore.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;
        theProjectionName = theStore.AllShards().Single().Name.Name;

        theDatabase = (InMemoryEventDatabase)(await theStore.AllDatabases()).Single();

        var instrumented = Substitute.For<IEventStore>();
        instrumented.ExtendedProgressionEnabled.Returns(true);
        theWriter = new ExtendedProgressionWriter(instrumented, theWrites, TimeProvider.System, NullLogger.Instance);
        theWriter.PeriodicHeartbeatsEnabled.ShouldBeFalse();
        theSubscription = theDatabase.Tracker.Subscribe(theWriter);

        theDaemon = await theStore.BuildProjectionDaemonAsync();
        await theDaemon.StartAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in thePoisoned) ParkTally.Poisoned.TryRemove(id, out _);

        await theDaemon.StopAllAsync();
        theDaemon.Dispose();
        theSubscription.Dispose();
        await theWriter.DisposeAsync();
    }

    private void poison(Guid id)
    {
        thePoisoned.Add(id);
        ParkTally.Poisoned[id] = true;
    }

    private async Task<Guid> appendAsync(Guid? id, params object[] events)
    {
        var stream = id ?? Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        if (id.HasValue) session.Events.Append(stream, events);
        else session.Events.StartStream<ParkTally>(stream, events);
        await session.SaveChangesAsync();
        return stream;
    }

    [Fact]
    public async Task a_park_a_replay_and_a_discard_each_write_the_parked_count()
    {
        var replayed = await appendAsync(null, new Counted());
        var discarded = await appendAsync(null, new Counted());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        poison(replayed);
        poison(discarded);
        await appendAsync(replayed, new CountedPoison());
        await appendAsync(discarded, new CountedPoison());
        await theDatabase.WaitForNonStaleProjectionDataAsync(Timeout);

        (await theDatabase.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), TestContext.Current.CancellationToken)).Count.ShouldBe(2);
        await theWrites.WaitForParkedCount(ShardAction.Parked, 2);

        ParkTally.Poisoned.TryRemove(replayed, out _);
        (await theDaemon.ReplayParkedIdentityAsync(theProjectionName, null, replayed, TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ParkedReplayOutcome.Replayed);
        await theWrites.WaitForParkedCount(ShardAction.Unparked, 1);

        (await theDaemon.DiscardParkedIdentityAsync(theProjectionName, null, discarded, TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ParkedReplayOutcome.Discarded);
        await theWrites.WaitForParkedCount(ShardAction.Unparked, 0);

        // What a store persists is the last write: nothing parked
        theWrites.Latest()!.ParkedCount.ShouldBe(0);
    }

    private class RecordingDatabase : IEventDatabase
    {
        private readonly List<ShardState> _writes = new();

        public Task WriteExtendedProgressionAsync(ShardState state, CancellationToken token = default)
        {
            lock (_writes)
            {
                _writes.Add(state);
            }

            return Task.CompletedTask;
        }

        public ShardState? Latest()
        {
            lock (_writes)
            {
                return _writes.LastOrDefault();
            }
        }

        public async Task WaitForParkedCount(ShardAction action, int count)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (true)
            {
                lock (_writes)
                {
                    if (_writes.Any(x => x.Action == action && x.ParkedCount == count)) return;
                }

                if (timeout.IsCancellationRequested)
                {
                    List<string> written;
                    lock (_writes)
                    {
                        written = _writes.Select(x => $"{x.Action}/{x.ParkedCount?.ToString() ?? "null"}").ToList();
                    }

                    throw new TimeoutException(
                        $"No {action} write with a parked count of {count}. Written: {string.Join(", ", written)}");
                }

                await Task.Delay(25);
            }
        }

        public string Identifier => "recording";
        public Uri DatabaseUri => new("db://recording");
        public ShardStateTracker Tracker => null!;

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent,
            CancellationToken token) => Task.CompletedTask;

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token) => Task.CompletedTask;

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => Task.CompletedTask;

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => Task.FromResult(0L);

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => Task.FromResult<long?>(null);

        public string StorageIdentifier => "recording";

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token) => Task.FromResult(0L);

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<ShardState>>([]);
    }
}
