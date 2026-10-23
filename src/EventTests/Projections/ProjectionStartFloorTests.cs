using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#809: where a continuous start and a rebuild begin, given the configured strategy and the
// persisted per-database start floor.
public class ProjectionStartFloorTests
{
    private static readonly ShardName Trips = new("Trips");
    private static readonly CancellationToken Token = CancellationToken.None;

    private static IEventDatabase databaseWith(long? storedFloor = null, long progress = 0)
    {
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");
        database.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(storedFloor);
        database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(progress);
        return database;
    }

    private static IEventDatabase databaseWithoutTheSeam(long progress = 0)
    {
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("db1");
        database.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException());
        database.TrySetProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException());
        database.ProjectionProgressFor(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(progress);
        return database;
    }

    // ------------------------------------------------------------------ rebuild (the pre-existing bug)

    [Fact]
    public async Task a_rebuild_starts_at_zero_by_default()
    {
        (await new AsyncOptions().DetermineRebuildFloorAsync(Trips, databaseWith(), Token)).ShouldBe(0);
    }

    [Fact]
    public async Task a_rebuild_honours_a_configured_sequence_floor()
    {
        var options = new AsyncOptions().SubscribeFromSequence(1000);
        (await options.DetermineRebuildFloorAsync(Trips, databaseWith(), Token)).ShouldBe(1000);
    }

    [Fact]
    public async Task a_rebuild_honours_a_configured_time_floor()
    {
        var at = DateTimeOffset.UtcNow.AddDays(-3);
        var database = databaseWith();
        database.FindEventStoreFloorAtTimeAsync(at, Arg.Any<CancellationToken>()).Returns(777L);

        var options = new AsyncOptions().SubscribeFromTime(at);
        (await options.DetermineRebuildFloorAsync(Trips, database, Token)).ShouldBe(777);
    }

    [Fact]
    public async Task a_rebuild_ignores_a_floor_configured_for_another_database()
    {
        var options = new AsyncOptions().SubscribeFromSequence(1000, "other");
        (await options.DetermineRebuildFloorAsync(Trips, databaseWith(), Token)).ShouldBe(0);
    }

    [Fact]
    public async Task a_rebuild_starts_from_a_stored_floor()
    {
        (await new AsyncOptions().DetermineRebuildFloorAsync(Trips, databaseWith(storedFloor: 500), Token)).ShouldBe(500);
    }

    // ------------------------------------------------------------------ a stored floor wins

    [Fact]
    public async Task a_stored_floor_wins_over_a_configured_sequence_floor()
    {
        var options = new AsyncOptions().SubscribeFromSequence(1000);
        var database = databaseWith(storedFloor: 500, progress: 200);

        (await options.DetermineRebuildFloorAsync(Trips, database, Token)).ShouldBe(500);

        var position = await options.DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, database, Token);
        position.Floor.ShouldBe(500);
        position.ShouldUpdateProgressFirst.ShouldBeTrue();
    }

    [Fact]
    public async Task progress_past_a_stored_floor_is_kept()
    {
        var position = await new AsyncOptions().DetermineStartingPositionAsync(5000, Trips,
            ShardExecutionMode.Continuous, databaseWith(storedFloor: 500, progress: 900), Token);

        position.Floor.ShouldBe(900);
        position.ShouldUpdateProgressFirst.ShouldBeFalse();
    }

    [Fact]
    public async Task from_present_ignores_a_stored_floor()
    {
        var database = databaseWith(storedFloor: 500);
        var position = await new AsyncOptions().SubscribeFromPresent()
            .DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(5000);
        await database.DidNotReceive().FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_store_without_the_seam_keeps_the_configured_behaviour()
    {
        var options = new AsyncOptions().SubscribeFromSequence(1000);
        var database = databaseWithoutTheSeam(progress: 10);

        (await options.DetermineRebuildFloorAsync(Trips, database, Token)).ShouldBe(1000);
        (await options.DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, database, Token))
            .Floor.ShouldBe(1000);
        (await new AsyncOptions().DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, database, Token))
            .Floor.ShouldBe(10);
    }

    // ------------------------------------------------------------------ SubscribeFromFirstRun

    [Fact]
    public async Task the_first_run_pins_the_high_water_as_the_floor()
    {
        var database = databaseWith();
        database.TrySetProjectionFloorAsync("Trips", null, 5000, Arg.Any<CancellationToken>()).Returns(5000L);

        var position = await new AsyncOptions().SubscribeFromFirstRun()
            .DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(5000);
        position.ShouldUpdateProgressFirst.ShouldBeTrue();
        position.PinnedFloor.ShouldBe(5000);
    }

    [Fact]
    public async Task concurrent_first_runs_all_use_the_stored_winner()
    {
        // Another node pinned 4000 first; this node's own high water of 5000 loses
        var database = databaseWith();
        database.TrySetProjectionFloorAsync("Trips", null, 5000, Arg.Any<CancellationToken>()).Returns(4000L);

        var position = await new AsyncOptions().SubscribeFromFirstRun()
            .DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(4000);
    }

    [Fact]
    public async Task a_later_start_reads_the_stored_floor_and_pins_nothing()
    {
        var database = databaseWith(storedFloor: 4000, progress: 4500);

        var position = await new AsyncOptions().SubscribeFromFirstRun()
            .DetermineStartingPositionAsync(9000, Trips, ShardExecutionMode.Continuous, database, Token);

        position.Floor.ShouldBe(4500);
        position.PinnedFloor.ShouldBeNull();
        await database.DidNotReceive().TrySetProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_first_run_projection_rebuilds_from_its_floor()
    {
        (await new AsyncOptions().SubscribeFromFirstRun().DetermineRebuildFloorAsync(Trips, databaseWith(storedFloor: 4000), Token))
            .ShouldBe(4000);
    }

    [Fact]
    public async Task first_run_on_a_store_without_the_seam_fails_loudly()
    {
        await Should.ThrowAsync<NotSupportedException>(() => new AsyncOptions().SubscribeFromFirstRun()
            .DetermineStartingPositionAsync(5000, Trips, ShardExecutionMode.Continuous, databaseWithoutTheSeam(), Token));
    }

    [Fact]
    public async Task the_floor_is_keyed_by_projection_name_without_version_plus_tenant()
    {
        var database = databaseWith();
        database.TrySetProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<long>(2));
        var v2ForTenant = ShardName.Compose("Trips", tenantId: "acme", version: 2);

        await new AsyncOptions().SubscribeFromFirstRun()
            .DetermineStartingPositionAsync(42, v2ForTenant, ShardExecutionMode.Continuous, database, Token);

        await database.Received(1).FetchProjectionFloorAsync("Trips", "acme", Arg.Any<CancellationToken>());
        await database.Received(1).TrySetProjectionFloorAsync("Trips", "acme", 42, Arg.Any<CancellationToken>());
    }
}
