using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#1046 — <c>IEventDatabase.WaitForNonStaleProjectionDataAsync</c> as a default interface method,
/// lifted from Polecat and Fisher. What "current" means is pinned on
/// <see cref="ProjectionLagCalculator.StaleCells" /> in <see cref="ProjectionLagTests" />; these pin the wait
/// around it. The timeout path is observable, because callers including the daemon and Bobcat catch
/// <see cref="TimeoutException" />.
/// </summary>
public class NonStaleProjectionWaitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    private static readonly ShardName Trips = ShardName.Compose("Trips");
    private static readonly ShardName Orders = ShardName.Compose("Orders");

    private static ShardState row(string identity, long sequence) => new(identity, sequence);

    private static WaitDatabase databaseAt(long head, params ShardState[] progress)
        => new([Trips, Orders]) { Head = head, Progress = progress };

    [Fact]
    public async Task returns_once_every_registered_shard_has_reached_the_head()
    {
        IEventDatabase database = databaseAt(10, row("Trips:All", 10), row("Orders:All", 12));

        await database.WaitForNonStaleProjectionDataAsync(Generous);
    }

    [Fact]
    public async Task keeps_polling_until_the_shards_catch_up()
    {
        var database = databaseAt(10, row("Trips:All", 3), row("Orders:All", 10));
        database.OnProgressRead = reads =>
        {
            if (reads == 3) database.Progress = [row("Trips:All", 10), row("Orders:All", 10)];
        };

        await ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Generous);

        database.ProgressReads.ShouldBe(3);
    }

    [Fact]
    public async Task no_registered_shards_means_nothing_can_be_stale_and_nothing_is_read()
    {
        var database = new WaitDatabase([]) { Head = 10 };

        await ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(TimeSpan.Zero);

        database.HeadReads.ShouldBe(0);
        database.ProgressReads.ShouldBe(0);
    }

    [Fact]
    public async Task a_store_with_no_events_returns_at_once_even_with_shards_that_never_ran()
    {
        // bobcat#497: Marten throws here, so the first read of a fresh schema failed where every later one
        // passed. Polecat and Fisher return, and nothing can be stale.
        var database = databaseAt(0);

        await ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Generous);

        database.ProgressReads.ShouldBe(0);
    }

    [Fact]
    public async Task the_head_is_read_once_so_sustained_appends_cannot_hold_the_wait_open()
    {
        // A ceiling re-read per poll is a moving target. Polecat's and Fisher's would time out here.
        var database = databaseAt(10, row("Trips:All", 10), row("Orders:All", 10));
        database.OnProgressRead = _ => database.Head += 5;

        await ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Generous);

        database.HeadReads.ShouldBe(1);
    }

    [Fact]
    public async Task times_out_naming_who_is_behind_and_who_never_reported()
    {
        // fisher#102 / polecat#602: Orders has no row, beside a sibling that has one
        IEventDatabase database = databaseAt(10, row("Trips:All", 3));

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            database.WaitForNonStaleProjectionDataAsync(Short));

        ex.Message.ShouldContain("Trips:All:3");
        ex.Message.ShouldContain("no progress recorded: [Orders:All]");
        ex.Message.ShouldContain("sequence 10");
        ex.Message.ShouldContain("wait-db");
    }

    [Fact]
    public async Task with_no_agent_state_at_all_the_timeout_says_the_daemon_may_not_be_running()
    {
        IEventDatabase database = databaseAt(10);

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            database.WaitForNonStaleProjectionDataAsync(Short));

        ex.Message.ShouldContain("the daemon may not be running");
        ex.Message.ShouldNotContain("Agents:");
    }

    [Fact]
    public async Task the_timeout_reports_what_the_daemon_last_said_about_a_stale_shard()
    {
        // fisher#329: a timeout that names a shard and nothing else cannot tell slow from stopped, and
        // "the daemon may not be running" beside a sibling at the head points the wrong way
        var database = databaseAt(10, row("Trips:All", 3), row("Orders:All", 10));

        var paused = new ShardState("Trips:All", 3)
        {
            AgentStatus = "Paused",
            PauseReason = "Failure: boom\n   at Some.Frame()\n ---> Inner: why\n   at Another.Frame()"
        };
        ((IObserver<ShardState>)database.Tracker).OnNext(paused);

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Short));

        ex.Message.ShouldContain("agent Paused");
        ex.Message.ShouldContain("Failure: boom ---> Inner: why");
        ex.Message.ShouldNotContain("Some.Frame");
        ex.Message.ShouldNotContain("the daemon may not be running");
    }

    [Fact]
    public async Task a_tracker_that_cannot_be_read_does_not_replace_the_timeout()
    {
        var database = databaseAt(10, row("Trips:All", 3));
        database.TrackerFailure = new InvalidOperationException("no tracker here");

        await Should.ThrowAsync<TimeoutException>(() =>
            ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Short));
    }

    [Fact]
    public async Task a_timeout_of_zero_is_still_a_timeout()
    {
        // Fisher pins this one: the clock elapses before the first read, not in the delay
        IEventDatabase database = databaseAt(10, row("Trips:All", 3));

        await Should.ThrowAsync<TimeoutException>(() =>
            database.WaitForNonStaleProjectionDataAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task the_clock_elapsing_inside_a_read_is_a_timeout_not_a_cancellation()
    {
        // fisher#7: the same condition must not surface as two exception types depending on timing
        var database = databaseAt(10);
        database.OnProgressReadAsync = token => Task.Delay(Timeout.InfiniteTimeSpan, token);

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Short));

        ex.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        ex.Message.ShouldContain("could not be read");
    }

    [Fact]
    public async Task the_clock_elapsing_inside_the_first_read_is_a_timeout_too()
    {
        // Marten's cold start: a first EnsureStorageExists can take the whole budget
        var database = databaseAt(10);
        database.OnHeadReadAsync = token => Task.Delay(Timeout.InfiniteTimeSpan, token);

        await Should.ThrowAsync<TimeoutException>(() =>
            ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Short));
    }

    [Fact]
    public async Task a_provider_that_reports_cancellation_in_its_own_exception_is_a_timeout()
    {
        // SqlClient surfaces a cancelled command as a bare SqlException, which is not an
        // OperationCanceledException and does not wrap one. Polecat dodged it by not passing a token, which
        // left a hung read unbounded by the timeout.
        var database = databaseAt(10);
        database.OnProgressReadAsync = async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                throw new ProviderException("A severe error occurred on the current command");
            }
        };

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Short));

        ex.InnerException.ShouldBeOfType<ProviderException>();
    }

    [Fact]
    public async Task a_real_failure_before_the_timeout_is_not_disguised_as_one()
    {
        var database = databaseAt(10);
        database.OnProgressReadAsync = _ => throw new ProviderException("the table is gone");

        await Should.ThrowAsync<ProviderException>(() =>
            ((IEventDatabase)database).WaitForNonStaleProjectionDataAsync(Generous));
    }

    [Fact]
    public async Task a_stores_own_implementation_keeps_winning()
    {
        IEventDatabase database = new DatabaseWithItsOwnWait();

        // RegisteredShardNames is not implemented here, so reaching the default would fail this
        await database.WaitForNonStaleProjectionDataAsync(Generous);

        ((DatabaseWithItsOwnWait)database).Waited.ShouldBeTrue();
    }

    [Fact]
    public async Task a_store_with_no_registered_shard_seam_says_so_rather_than_reporting_current()
    {
        // Empty means nothing runs asynchronously. Borrowing that answer would turn "this store has not
        // implemented the seam" into "every projection is current".
        IEventDatabase database = new DatabaseWithoutTheSeam();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            database.WaitForNonStaleProjectionDataAsync(Generous));

        ex.Message.ShouldContain(nameof(IEventDatabase.RegisteredShardNames));
        ex.Message.ShouldContain(nameof(DatabaseWithoutTheSeam));
    }

    [Fact]
    public async Task a_store_that_keeps_a_public_method_on_its_class_can_forward_to_the_wait()
    {
        // A default interface member is not reachable through the class, and a public method of the same
        // shape IS the interface's implementation, so this is the only forward that does not recurse
        var database = new WaitDatabase([Trips]) { Head = 10, Progress = [row("Trips:All", 3)] };

        await Should.ThrowAsync<TimeoutException>(() =>
            NonStaleProjectionWait.WaitAsync(database, database.Registered, Short));
    }

    private sealed class ProviderException(string message) : Exception(message);

    // Implements only what the interface still requires, plus the seam. The wait is deliberately not
    // written here: the point is that a store reaches it through the interface.
    private sealed class WaitDatabase(IReadOnlyList<ShardName> registered) : EventDatabaseStub, IEventDatabase
    {
        private readonly ShardStateTracker _tracker = new(NullLogger.Instance);

        public IReadOnlyList<ShardName> Registered => registered;

        public long Head { get; set; }
        public IReadOnlyList<ShardState> Progress { get; set; } = [];
        public int HeadReads { get; private set; }
        public int ProgressReads { get; private set; }
        public Action<int>? OnProgressRead { get; set; }
        public Func<CancellationToken, Task>? OnProgressReadAsync { get; set; }
        public Func<CancellationToken, Task>? OnHeadReadAsync { get; set; }
        public Exception? TrackerFailure { get; set; }

        public IReadOnlyList<ShardName> RegisteredShardNames() => registered;

        public override ShardStateTracker Tracker => TrackerFailure != null ? throw TrackerFailure : _tracker;

        public override async Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
        {
            HeadReads++;
            token.ThrowIfCancellationRequested();
            if (OnHeadReadAsync != null) await OnHeadReadAsync(token);

            return Head;
        }

        public override async Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
        {
            ProgressReads++;
            token.ThrowIfCancellationRequested();
            if (OnProgressReadAsync != null) await OnProgressReadAsync(token);
            OnProgressRead?.Invoke(ProgressReads);

            return Progress;
        }
    }

    private sealed class DatabaseWithItsOwnWait : EventDatabaseStub, IEventDatabase
    {
        public bool Waited { get; private set; }

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
        {
            Waited = true;
            return Task.CompletedTask;
        }
    }

    private sealed class DatabaseWithoutTheSeam : EventDatabaseStub;

    // Base members virtual so each fake says only what it uses. Not the interface's defaults, because
    // nothing here should be able to reach a member the test did not mean to.
    private abstract class EventDatabaseStub : IEventDatabase
    {
        public string Identifier => "wait-db";
        public Uri DatabaseUri => throw new NotImplementedException();
        public virtual ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();

        public virtual Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
            => throw new NotImplementedException();

        public virtual Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => throw new NotImplementedException();
    }
}
