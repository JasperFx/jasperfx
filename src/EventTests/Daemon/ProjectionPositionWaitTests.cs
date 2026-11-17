using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// marten#6004: read-your-writes. <see cref="ProjectionPositionWait" /> waits for the caller's own position in
/// the progression table, not for the store's highest sequence, and answers with a value rather than throwing
/// on timeout.
/// </summary>
public class ProjectionPositionWaitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    private static readonly ShardName Trips = ShardName.Compose("Trips");
    private static readonly ShardName Orders = ShardName.Compose("Orders");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ShardState row(string identity, long sequence) => new(identity, sequence);

    private static Task<ProjectionWaitResult> wait(ProgressDatabase database, EventStorePosition position,
        TimeSpan timeout, params ShardName[] shards)
        => ProjectionPositionWait.WaitAsync(database, position, shards.Length == 0 ? [Trips, Orders] : shards,
            timeout, Fast, Token);

    [Fact]
    public async Task reached_when_every_shard_has_processed_the_position()
    {
        var database = new ProgressDatabase { Progress = [row("Trips:All", 10), row("Orders:All", 12)] };

        var result = await wait(database, new EventStorePosition(null, null, 10), Generous);

        result.Reached.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.Status.ShouldBe(ProjectionWaitStatus.Reached);
        result.Shards.Select(x => x.Sequence).ShouldBe([10L, 12L]);
    }

    [Fact]
    public async Task other_writers_ahead_of_the_position_do_not_hold_the_caller()
    {
        // The store's head is far past the caller's events and the shards are short of it. Only the caller's
        // position matters, so the head is never even read.
        var database = new ProgressDatabase
        {
            Head = 5000,
            Progress = [row("Trips:All", 42), row("Orders:All", 40), row("HighWaterMark", 4000)]
        };

        var result = await wait(database, new EventStorePosition(null, null, 40), Generous);

        result.Reached.ShouldBeTrue();
        database.HeadReads.ShouldBe(0);
    }

    [Fact]
    public async Task keeps_polling_until_the_shards_pass_the_position()
    {
        var database = new ProgressDatabase { Progress = [row("Trips:All", 3), row("Orders:All", 10)] };
        database.OnProgressRead = reads =>
        {
            if (reads == 3) database.Progress = [row("Trips:All", 10), row("Orders:All", 10)];
        };

        var result = await wait(database, new EventStorePosition(null, null, 10), Generous);

        result.Reached.ShouldBeTrue();
        database.ProgressReads.ShouldBe(3);
    }

    [Fact]
    public async Task a_timeout_is_a_result_carrying_how_far_each_shard_got()
    {
        var database = new ProgressDatabase { Progress = [row("Trips:All", 3)] };

        var result = await wait(database, new EventStorePosition(null, null, 10), Short);

        result.TimedOut.ShouldBeTrue();
        result.Reached.ShouldBeFalse();
        result.Elapsed.ShouldBeGreaterThanOrEqualTo(Short - TimeSpan.FromMilliseconds(50));

        var trips = result.Shards.Single(x => x.Shard.Name == "Trips");
        trips.Sequence.ShouldBe(3);
        trips.Reached.ShouldBeFalse();

        var orders = result.Shards.Single(x => x.Shard.Name == "Orders");
        orders.Sequence.ShouldBeNull();

        result.ToString().ShouldContain("Trips:All at 3");
        result.ToString().ShouldContain("Orders:All with no progress recorded");
    }

    [Fact]
    public async Task a_zero_timeout_reads_once_and_answers()
    {
        var behind = new ProgressDatabase { Progress = [row("Trips:All", 3), row("Orders:All", 10)] };
        (await wait(behind, new EventStorePosition(null, null, 10), TimeSpan.Zero)).TimedOut.ShouldBeTrue();
        behind.ProgressReads.ShouldBe(1);

        var current = new ProgressDatabase { Progress = [row("Trips:All", 10), row("Orders:All", 10)] };
        (await wait(current, new EventStorePosition(null, null, 10), TimeSpan.Zero)).Reached.ShouldBeTrue();
    }

    [Fact]
    public async Task nothing_appended_or_nothing_to_wait_on_is_reached_without_a_read()
    {
        var database = new ProgressDatabase();

        (await wait(database, new EventStorePosition(null, null, 0), Generous)).Reached.ShouldBeTrue();
        (await ProjectionPositionWait.WaitAsync(database, new EventStorePosition(null, null, 10), [], Generous, Token)).Reached
            .ShouldBeTrue();

        database.ProgressReads.ShouldBe(0);
    }

    [Fact]
    public async Task a_paused_shard_returns_at_once_with_that_state()
    {
        var paused = row("Trips:All", 3);
        paused.AgentStatus = "Paused";
        var database = new ProgressDatabase { Progress = [paused, row("Orders:All", 10)] };

        var result = await wait(database, new EventStorePosition(null, null, 10), Generous);

        result.Status.ShouldBe(ProjectionWaitStatus.Paused);
        result.Shards.Single(x => x.Shard.Name == "Trips").Paused.ShouldBeTrue();
        database.ProgressReads.ShouldBe(1);
    }

    [Fact]
    public async Task a_shard_this_processes_daemon_paused_returns_at_once_too()
    {
        // The progression row only carries the agent status with extended progression tracking on
        var database = new ProgressDatabase { Progress = [row("Trips:All", 3), row("Orders:All", 10)] };
        ((IObserver<ShardState>)database.Tracker).OnNext(new ShardState("Trips:All", 3) { Action = ShardAction.Paused });

        var result = await wait(database, new EventStorePosition(null, null, 10), Generous);

        result.Status.ShouldBe(ProjectionWaitStatus.Paused);
    }

    [Fact]
    public async Task a_paused_shard_that_already_passed_the_position_does_not_matter()
    {
        var paused = row("Trips:All", 12);
        paused.AgentStatus = "Paused";
        var database = new ProgressDatabase { Progress = [paused, row("Orders:All", 10)] };

        (await wait(database, new EventStorePosition(null, null, 10), Generous)).Reached.ShouldBeTrue();
    }

    [Fact]
    public async Task a_rebuilding_shard_returns_at_once_with_that_state()
    {
        var rebuilding = row("Trips:All", 3);
        rebuilding.Mode = ShardMode.rebuilding;
        var database = new ProgressDatabase { Progress = [rebuilding, row("Orders:All", 10)] };

        var result = await wait(database, new EventStorePosition(null, null, 10), Generous);

        result.Status.ShouldBe(ProjectionWaitStatus.Rebuilding);
        result.Shards.Single(x => x.Shard.Name == "Trips").Rebuilding.ShouldBeTrue();
    }

    [Fact]
    public async Task a_tenant_position_reads_that_tenants_rows_on_a_store_partitioned_by_tenant()
    {
        // Each tenant draws its own sequence, so tenant b's 5 says nothing about tenant a's 7
        var database = new ProgressDatabase
        {
            Progress = [row("Trips:All:a", 7), row("Trips:All:b", 5), row("HighWaterMark:a", 7)]
        };

        var reached = await wait(database, new EventStorePosition(null, "a", 7), Short, Trips);
        reached.Reached.ShouldBeTrue();
        reached.Shards.Single().Shard.Identity.ShouldBe("Trips:All:a");

        var behind = await wait(database, new EventStorePosition(null, "b", 7), Short, Trips);
        behind.TimedOut.ShouldBeTrue();
        behind.Shards.Single().Sequence.ShouldBe(5);
    }

    [Fact]
    public async Task a_tenant_with_no_row_yet_on_a_partitioned_store_is_behind()
    {
        var database = new ProgressDatabase { Progress = [row("Trips:All:a", 7)] };

        var result = await wait(database, new EventStorePosition(null, "c", 1), Short, Trips);

        result.TimedOut.ShouldBeTrue();
        result.Shards.Single().Shard.Identity.ShouldBe("Trips:All:c");
        result.Shards.Single().Sequence.ShouldBeNull();
    }

    [Fact]
    public async Task a_tenant_position_on_a_store_with_one_sequence_reads_the_store_wide_row()
    {
        // Conjoined tenancy: the session has a tenant, the events share one sequence and one row per shard
        var database = new ProgressDatabase { Progress = [row("Trips:All", 9)] };

        var result = await wait(database, new EventStorePosition(null, "a", 9), Generous, Trips);

        result.Reached.ShouldBeTrue();
        result.Shards.Single().Shard.Identity.ShouldBe("Trips:All");
    }

    [Fact]
    public async Task every_slice_of_a_sliced_projection_has_to_pass()
    {
        var first = ShardName.Compose("Trips", "1");
        var second = ShardName.Compose("Trips", "2");
        var database = new ProgressDatabase { Progress = [row(first.Identity, 10), row(second.Identity, 4)] };

        var result = await wait(database, new EventStorePosition(null, null, 10), Short, first, second);

        result.TimedOut.ShouldBeTrue();
        result.Shards.Count(x => x.Reached).ShouldBe(1);
    }

    [Fact]
    public async Task the_callers_cancellation_throws_rather_than_reporting_a_timeout()
    {
        var database = new ProgressDatabase { Progress = [row("Trips:All", 3)] };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ProjectionPositionWait.WaitAsync(database, new EventStorePosition(null, null, 10), [Trips], Generous, Fast,
                cancellation.Token));
    }

    [Fact]
    public async Task the_clock_running_out_inside_a_provider_read_is_still_a_timeout()
    {
        // SqlClient reports a cancelled command as its own exception type (fisher#7)
        var database = new ProgressDatabase
        {
            OnProgressReadAsync = async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    throw new ProviderException("A severe error occurred on the current command");
                }
            }
        };

        var result = await wait(database, new EventStorePosition(null, null, 10), Short);

        result.TimedOut.ShouldBeTrue();
        result.Shards.ShouldAllBe(x => x.Sequence == null);
    }

    [Fact]
    public async Task a_real_failure_before_the_timeout_propagates()
    {
        var database = new ProgressDatabase { OnProgressReadAsync = _ => throw new ProviderException("the table is gone") };

        await Should.ThrowAsync<ProviderException>(() => wait(database, new EventStorePosition(null, null, 10), Generous));
    }

    [Fact]
    public void shards_are_found_by_projection_name_and_include_every_slice()
    {
        var first = ShardName.Compose("Trips", "1");
        var second = ShardName.Compose("Trips", "2");

        ProjectionPositionWait.ShardsNamed([first, second, Orders], ["trips"]).ShouldBe([first, second]);
    }

    [Fact]
    public void an_unknown_projection_name_is_refused_rather_than_reported_current()
    {
        var ex = Should.Throw<ArgumentException>(() => ProjectionPositionWait.ShardsNamed([Trips, Orders], ["Nope"]));

        ex.Message.ShouldContain("'Nope'");
        ex.Message.ShouldContain("Trips");
    }

    [Fact]
    public async Task the_store_resolves_types_and_the_positions_database()
    {
        var other = new ProgressDatabase("other");
        var database = new ProgressDatabase("main") { Progress = [row("Trips:All", 10)] };
        IEventStore store = new WaitingEventStore([database, other], typeof(TripSummary), Trips);

        var result = await store.WaitForProjectionsAsync(new EventStorePosition("main", null, 10), [typeof(TripSummary)],
            Generous, Token);

        result.Reached.ShouldBeTrue();
        database.ProgressReads.ShouldBe(1);
        other.ProgressReads.ShouldBe(0);
    }

    [Fact]
    public async Task the_store_resolves_projection_names()
    {
        var database = new ProgressDatabase { Progress = [row("Trips:All", 10)] };
        IEventStore store = new WaitingEventStore([database], typeof(TripSummary), Trips);

        (await store.WaitForProjectionsAsync(new EventStorePosition(null, null, 10), ["Trips"], Generous, Token)).Reached
            .ShouldBeTrue();
    }

    [Fact]
    public async Task the_store_refuses_a_type_no_asynchronous_projection_publishes()
    {
        IEventStore store = new WaitingEventStore([new ProgressDatabase()], typeof(TripSummary), Trips);

        await Should.ThrowAsync<ArgumentException>(() =>
            store.WaitForProjectionsAsync(new EventStorePosition(null, null, 10), [typeof(string)], Generous, Token));
    }

    [Fact]
    public async Task the_store_refuses_a_database_it_does_not_have()
    {
        IEventStore store = new WaitingEventStore([new ProgressDatabase("main")], typeof(TripSummary), Trips);

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            store.WaitForProjectionsAsync(new EventStorePosition("gone", null, 10), [typeof(TripSummary)], Generous,
                Token));

        ex.Message.ShouldContain("main");
    }

    [Fact]
    public async Task a_store_that_cannot_map_types_to_shards_says_so()
    {
        IEventStore store = new WaitingEventStore([new ProgressDatabase()], null, Trips);

        await Should.ThrowAsync<NotSupportedException>(() =>
            store.WaitForProjectionsAsync(new EventStorePosition(null, null, 10), [typeof(TripSummary)], Generous, Token));
    }

    [Fact]
    public void a_session_that_does_not_record_its_position_has_none()
    {
        IStorageOperations operations = new SessionWithoutPosition();
        operations.LastAppendedPosition.ShouldBeNull();
#pragma warning disable CS0618
        operations.LastAppendedSequence.ShouldBeNull();
#pragma warning restore CS0618
    }

    [Fact]
    public void the_obsolete_last_appended_sequence_forwards_to_last_appended_position()
    {
        // jasperfx#1218: callers still on the 3.5.0 name read the position a store records under the new one
        var position = new EventStorePosition("main", "tenant1", 42);
        IStorageOperations operations = new SessionWithPosition(position);

        operations.LastAppendedPosition.ShouldBe(position);
#pragma warning disable CS0618
        operations.LastAppendedSequence.ShouldBe(position);
#pragma warning restore CS0618
    }

    [Fact]
    public void a_store_built_against_the_old_name_still_loads_and_has_no_position_under_the_new_one()
    {
        // A store compiled against JasperFx 3.5.0 implemented LastAppendedSequence. It keeps working for callers
        // of the old name, and the new name answers null (no position to wait on) until the store moves to it.
        var position = new EventStorePosition("main", null, 7);
        IStorageOperations operations = new SessionOnTheOldName(position);

#pragma warning disable CS0618
        operations.LastAppendedSequence.ShouldBe(position);
#pragma warning restore CS0618
        operations.LastAppendedPosition.ShouldBeNull();
    }

    private class SessionWithoutPosition : IStorageOperations
    {
        public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool EnableSideEffectsOnInlineProjections => false;

        public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SessionWithPosition(EventStorePosition position) : SessionWithoutPosition, IStorageOperations
    {
        public EventStorePosition? LastAppendedPosition => position;
    }

    private sealed class SessionOnTheOldName(EventStorePosition position) : SessionWithoutPosition, IStorageOperations
    {
        public EventStorePosition? LastAppendedSequence => position;
    }

    private sealed record TripSummary;

    private sealed class ProviderException(string message) : Exception(message);

    private sealed class ProgressDatabase(string identifier = "wait-db") : IEventDatabase
    {
        private readonly ShardStateTracker _tracker = new(NullLogger.Instance);

        public long Head { get; set; }
        public IReadOnlyList<ShardState> Progress { get; set; } = [];
        public int HeadReads { get; private set; }
        public int ProgressReads { get; private set; }
        public Action<int>? OnProgressRead { get; set; }
        public Func<CancellationToken, Task>? OnProgressReadAsync { get; set; }

        public string Identifier => identifier;
        public Uri DatabaseUri => new($"test://{identifier}");
        public ShardStateTracker Tracker => _tracker;
        public string StorageIdentifier => identifier;

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
        {
            HeadReads++;
            return Task.FromResult(Head);
        }

        public async Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
        {
            ProgressReads++;
            token.ThrowIfCancellationRequested();
            if (OnProgressReadAsync != null) await OnProgressReadAsync(token);
            OnProgressRead?.Invoke(ProgressReads);

            return Progress;
        }

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();
    }

    // Implements the shard lookup the generic interface would supply, and nothing else of the wait
    private sealed class WaitingEventStore(IEventDatabase[] databases, Type? projectedType, params ShardName[] shards)
        : IEventStore
    {
        public ValueTask<IReadOnlyList<IEventDatabase>> AllDatabases()
            => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>(databases);

        public IReadOnlyList<ShardName> RegisteredShardNames() => shards;

        public IReadOnlyList<ShardName> AsyncShardNamesFor(Type projectionOrPublishedType)
        {
            if (projectedType == null) throw new NotSupportedException("no shard lookup here");
            return projectionOrPublishedType == projectedType ? shards : [];
        }

        public DatabaseCardinality DatabaseCardinality =>
            databases.Length == 1 ? DatabaseCardinality.Single : DatabaseCardinality.StaticMultiple;

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
}

public class EventStorePositionTests
{
    [Theory]
    [InlineData(null, null, 1L)]
    [InlineData("main", null, 42L)]
    [InlineData(null, "tenant-a", 7L)]
    [InlineData("postgres://db1/marten", "*DEFAULT*", 9_000_000_000L)]
    [InlineData("a/b", "c d%", 3L)]
    public void the_text_form_round_trips(string? database, string? tenant, long sequence)
    {
        var position = new EventStorePosition(database, tenant, sequence);

        var text = position.ToString();
        text.ShouldNotContain(" ");
        text.Count(x => x == '/').ShouldBe(2);

        EventStorePosition.Parse(text).ShouldBe(position);
    }

    [Fact]
    public void a_position_with_no_database_or_tenant_is_short()
    {
        new EventStorePosition(null, null, 42).ToString().ShouldBe("//42");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("a/b/c")]
    [InlineData("a/b/-1")]
    [InlineData("a/b/1/2")]
    public void anything_else_is_not_a_position(string? text)
    {
        EventStorePosition.TryParse(text, out var position).ShouldBeFalse();
        position.ShouldBeNull();

        if (text != null) Should.Throw<FormatException>(() => EventStorePosition.Parse(text));
    }

    [Fact]
    public void the_position_serializes_as_json()
    {
        var position = new EventStorePosition("main", "tenant-a", 42);

        var json = JsonSerializer.Serialize(position);

        JsonSerializer.Deserialize<EventStorePosition>(json).ShouldBe(position);
    }
}
