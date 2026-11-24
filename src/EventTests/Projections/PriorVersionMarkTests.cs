using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1222: SubscribeFromPriorVersionMark starts a projection's new version at the same stored mark the
// backfill job stops at, captured once by whichever side gets there first.
public class PriorVersionMarkTests
{
    private const string Backfill = "line-total";
    private static readonly ShardName TripsV2 = new("Trips", ShardName.All, 2);
    private static readonly CancellationToken Token = CancellationToken.None;

    private static readonly string Key = EventRangeMarks.BackfillKey("Trips", Backfill);

    /// <summary>
    /// A database with in-memory marks, the given progression rows, and no stored projection floor.
    /// </summary>
    private static IEventDatabase databaseWith(Dictionary<string, long>? marks = null, params ShardState[] progress)
    {
        marks ??= new Dictionary<string, long>();
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");
        database.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((long?)null);
        database.AllProjectionProgress(Arg.Any<CancellationToken>()).Returns(progress);
        database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
            .Returns(call => progress.FirstOrDefault(x => x.ShardName == call.Arg<ShardName>().Identity)?.Sequence ?? 0);

        database.FetchEventRangeMarkAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => marks.TryGetValue(markKey(call.ArgAt<string>(0), call.ArgAt<string?>(1)), out var m)
                ? m
                : (long?)null);
        database.TrySetEventRangeMarkAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = markKey(call.ArgAt<string>(0), call.ArgAt<string?>(1));
                if (!marks.ContainsKey(key)) marks[key] = call.ArgAt<long>(2);
                return marks[key];
            });

        return database;
    }

    private static string markKey(string key, string? tenantId) => $"{key}|{tenantId}";

    [Fact]
    public async Task the_first_start_captures_the_prior_versions_progression_and_starts_there()
    {
        var marks = new Dictionary<string, long>();
        var database = databaseWith(marks, new ShardState("Trips:All", 900));

        var position = await new AsyncOptions().SubscribeFromPriorVersionMark(Backfill)
            .DetermineStartingPositionAsync(5000, TripsV2, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(900);
        position.ShouldUpdateProgressFirst.ShouldBeTrue();
        marks[markKey(Key, null)].ShouldBe(900);
    }

    [Fact]
    public async Task a_mark_the_backfill_job_stored_first_wins_over_the_prior_versions_later_progress()
    {
        var marks = new Dictionary<string, long> { [markKey(Key, null)] = 400 };
        var database = databaseWith(marks, new ShardState("Trips:All", 900));

        var position = await new AsyncOptions().SubscribeFromPriorVersionMark(Backfill)
            .DetermineStartingPositionAsync(5000, TripsV2, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(400);
    }

    [Fact]
    public async Task the_highest_prior_version_is_the_one_that_counts()
    {
        var database = databaseWith(null,
            new ShardState("Trips:All", 300),
            new ShardState("Trips:V3:All", 700),
            new ShardState("Other:All", 5000));

        var mark = await EventRangeMarks.CaptureBackfillCeilingAsync(database, new ShardName("Trips", ShardName.All, 4),
            Backfill, Token);

        mark.ShouldBe(700);
    }

    [Fact]
    public async Task progress_past_the_mark_is_kept()
    {
        var database = databaseWith(new Dictionary<string, long> { [markKey(Key, null)] = 400 },
            new ShardState("Trips:All", 900), new ShardState(TripsV2.Identity, 1200));

        var position = await new AsyncOptions().SubscribeFromPriorVersionMark(Backfill)
            .DetermineStartingPositionAsync(5000, TripsV2, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(1200);
        position.ShouldUpdateProgressFirst.ShouldBeFalse();
    }

    [Fact]
    public async Task without_a_prior_version_the_mark_is_the_shards_own_progression()
    {
        var trips = new ShardName("Trips");
        var database = databaseWith(null, new ShardState(trips.Identity, 650));

        (await EventRangeMarks.CaptureBackfillCeilingAsync(database, trips, Backfill, Token)).ShouldBe(650);
    }

    [Fact]
    public async Task a_stored_floor_below_the_mark_does_not_pull_the_start_back()
    {
        var database = databaseWith(new Dictionary<string, long> { [markKey(Key, null)] = 400 });
        database.FetchProjectionFloorAsync("Trips", null, Arg.Any<CancellationToken>()).Returns(100L);

        var position = await new AsyncOptions().SubscribeFromPriorVersionMark(Backfill)
            .DetermineStartingPositionAsync(5000, TripsV2, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(400);
    }

    [Fact]
    public async Task a_stored_floor_above_the_mark_still_wins()
    {
        var database = databaseWith(new Dictionary<string, long> { [markKey(Key, null)] = 400 });
        database.FetchProjectionFloorAsync("Trips", null, Arg.Any<CancellationToken>()).Returns(800L);

        var position = await new AsyncOptions().SubscribeFromPriorVersionMark(Backfill)
            .DetermineStartingPositionAsync(5000, TripsV2, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(800);
    }

    [Fact]
    public async Task a_rebuild_replays_from_zero()
    {
        var options = new AsyncOptions().SubscribeFromPriorVersionMark(Backfill);
        var database = databaseWith(new Dictionary<string, long> { [markKey(Key, null)] = 400 });

        (await options.DetermineRebuildFloorAsync(TripsV2, database, Token)).ShouldBe(0);
    }

    [Fact]
    public async Task each_tenant_gets_its_own_mark()
    {
        var marks = new Dictionary<string, long>();
        var database = databaseWith(marks,
            new ShardState(new ShardName("Trips", ShardName.All, 1, "a").Identity, 100),
            new ShardState(new ShardName("Trips", ShardName.All, 1, "b").Identity, 200));

        var options = new AsyncOptions().SubscribeFromPriorVersionMark(Backfill);
        (await options.DetermineStartingPositionAsync(0, new ShardName("Trips", ShardName.All, 2, "a"),
            ShardExecutionMode.Continuous, database, Token)).Floor.ShouldBe(100);
        (await options.DetermineStartingPositionAsync(0, new ShardName("Trips", ShardName.All, 2, "b"),
            ShardExecutionMode.Continuous, database, Token)).Floor.ShouldBe(200);

        marks[markKey(Key, "a")].ShouldBe(100);
        marks[markKey(Key, "b")].ShouldBe(200);
    }

    [Fact]
    public async Task a_store_without_the_marks_fails_the_start_rather_than_replaying_history()
    {
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");
        database.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((long?)null);
        database.FetchEventRangeMarkAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException());

        await Should.ThrowAsync<NotSupportedException>(() => new AsyncOptions()
            .SubscribeFromPriorVersionMark(Backfill)
            .DetermineStartingPositionAsync(5000, TripsV2, ShardExecutionMode.Continuous, database, Token));
    }

    [Fact]
    public async Task a_backfill_run_from_the_marks_resumes_after_the_stored_cursor()
    {
        var marks = new Dictionary<string, long>
        {
            [markKey(Key, null)] = 400,
            [markKey(EventRangeMarks.CursorKey(Key), null)] = 150
        };

        var run = await BackfillRun.FromMarksAsync(databaseWith(marks), TripsV2, Backfill, null, Token);

        run.ProjectionName.ShouldBe("Trips");
        run.CeilingKey.ShouldBe("backfill:Trips:line-total");
        run.CursorKey.ShouldBe("backfill:Trips:line-total:cursor");
        run.Cursors.ShouldHaveSingleItem().ShouldBe(new EventRangeCursor(null, 150, 400));
    }

    [Fact]
    public void the_mark_keys()
    {
        EventRangeMarks.RewriteKey("encrypt").ShouldBe("rewrite:encrypt");
        EventRangeMarks.BackfillKey("Trips", "year").ShouldBe("backfill:Trips:year");
        EventRangeMarks.CursorKey("rewrite:encrypt").ShouldBe("rewrite:encrypt:cursor");
    }

    // jasperfx#1293: a store can tell, at configuration time, whether SubscribeFromPriorVersionMark was called
    [Fact]
    public void starts_from_prior_version_mark_is_false_by_default()
    {
        new AsyncOptions().StartsFromPriorVersionMark.ShouldBeFalse();
    }

    [Fact]
    public void starts_from_prior_version_mark_is_false_for_the_other_starting_positions()
    {
        var options = new AsyncOptions();
        options.SubscribeFromPresent();
        options.SubscribeFromSequence(100, "db2");
        options.SubscribeFromTime(DateTimeOffset.UtcNow, "db3");
        options.SubscribeFromFirstRun("db4");
        options.SubscribeAsInlineToAsync();

        options.StartsFromPriorVersionMark.ShouldBeFalse();
    }

    [Fact]
    public void starts_from_prior_version_mark_is_true_for_every_database()
    {
        new AsyncOptions().SubscribeFromPriorVersionMark(Backfill).StartsFromPriorVersionMark.ShouldBeTrue();
    }

    [Fact]
    public void starts_from_prior_version_mark_is_true_for_only_one_database()
    {
        var options = new AsyncOptions();
        options.SubscribeFromPresent();
        options.SubscribeFromPriorVersionMark(Backfill, "db2");

        options.StartsFromPriorVersionMark.ShouldBeTrue();
    }
}
