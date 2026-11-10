using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#619 — the supported per-tenant projection lag read. Every rule pinned here exists
/// because a real deployment broke on its absence; the issue references are the incidents.
/// </summary>
public class ProjectionLagTests
{
    private static ShardState row(string identity, long sequence) => new(identity, sequence);

    private static IReadOnlyList<ProjectionLag> calculate(IEnumerable<ShardName> shards,
        params ShardState[] progress)
        => ProjectionLagCalculator.Calculate(shards, progress, "db1");

    [Fact]
    public void lag_is_the_distance_to_the_mark()
    {
        var lag = new ProjectionLag(ShardName.Compose("Trips"), "db1", 40, 100, true);
        lag.Lag.ShouldBe(60);
        lag.IsCaughtUp.ShouldBeFalse();
    }

    [Fact]
    public void lag_never_goes_negative_when_a_shard_reads_past_a_stale_mark()
    {
        new ProjectionLag(ShardName.Compose("Trips"), "db1", 120, 100, true).Lag.ShouldBe(0);
    }

    [Fact]
    public void a_cell_with_no_row_is_never_caught_up()
    {
        // The ambiguity the HasProgressionRow field exists to kill: a readiness probe that
        // conflates "never started" with "at zero" latches green during a version bump.
        var lag = new ProjectionLag(ShardName.Compose("Trips"), "db1", 0, 0, false);
        lag.Lag.ShouldBe(0);
        lag.IsCaughtUp.ShouldBeFalse();
    }

    [Fact]
    public void store_global_store_correlates_each_registered_shard_against_the_global_mark()
    {
        var lags = calculate([ShardName.Compose("Trips"), ShardName.Compose("Orders")],
            row(ShardState.HighWaterMark, 100),
            row("Trips:All", 90),
            row("Orders:All", 100));

        lags.Count.ShouldBe(2);
        lags.Single(x => x.Shard.Name == "Trips").Lag.ShouldBe(10);
        lags.Single(x => x.Shard.Name == "Orders").IsCaughtUp.ShouldBeTrue();
        lags.ShouldAllBe(x => x.DatabaseIdentifier == "db1");
    }

    [Fact]
    public void a_registered_shard_with_no_row_at_all_is_fully_behind()
    {
        var lags = calculate([ShardName.Compose("Trips")], row(ShardState.HighWaterMark, 100));

        var lag = lags.ShouldHaveSingleItem();
        lag.HasProgressionRow.ShouldBeFalse();
        lag.Sequence.ShouldBe(0);
        lag.HighWaterMark.ShouldBe(100);
        lag.Lag.ShouldBe(100);
        lag.IsCaughtUp.ShouldBeFalse();
    }

    [Fact]
    public void a_prior_versions_row_is_never_borrowed_by_the_current_version()
    {
        // The blue/green trap: V2's row still sits at the mark, but V3 is what is registered.
        var lags = calculate([ShardName.Compose("Trips", version: 3)],
            row(ShardState.HighWaterMark, 100),
            row("Trips:V2:All", 100));

        var lag = lags.ShouldHaveSingleItem();
        lag.Shard.Version.ShouldBe(3u);
        lag.HasProgressionRow.ShouldBeFalse();
        lag.IsCaughtUp.ShouldBeFalse();
        lag.Lag.ShouldBe(100);
    }

    [Fact]
    public void non_shard_bookkeeping_rows_are_excluded()
    {
        // marten#5161: rows that are not projection shards never advance, and reporting them as
        // projections that are permanently behind is what broke the reporter's status page.
        var lags = calculate([ShardName.Compose("Trips")],
            row(ShardState.HighWaterMark, 100),
            row("Trips:All", 100),
            row("some_bookkeeping_row", 0));

        lags.ShouldHaveSingleItem().Shard.Name.ShouldBe("Trips");
    }

    [Fact]
    public void each_tenant_is_measured_against_its_own_high_water_mark()
    {
        // marten#4761: under per-tenant event partitioning every tenant draws its own sequence,
        // so a store-global bar attributes one tenant's height to all of them.
        var lags = calculate([ShardName.Compose("Trips")],
            row(ShardState.HighWaterMark, 5000),
            row("HighWaterMark:acme", 100),
            row("HighWaterMark:zeta", 20),
            row("Trips:All:acme", 100),
            row("Trips:All:zeta", 5));

        lags.Count.ShouldBe(2);

        var acme = lags.Single(x => x.Shard.TenantId == "acme");
        acme.HighWaterMark.ShouldBe(100);
        acme.IsCaughtUp.ShouldBeTrue();

        var zeta = lags.Single(x => x.Shard.TenantId == "zeta");
        zeta.HighWaterMark.ShouldBe(20);
        zeta.Lag.ShouldBe(15);
    }

    [Fact]
    public void a_tenant_with_no_row_for_a_registered_shard_is_fully_behind_not_missing()
    {
        var lags = calculate([ShardName.Compose("Trips")],
            row("HighWaterMark:acme", 100),
            row("HighWaterMark:zeta", 60),
            row("Trips:All:acme", 100));

        lags.Count.ShouldBe(2);
        var zeta = lags.Single(x => x.Shard.TenantId == "zeta");
        zeta.HasProgressionRow.ShouldBeFalse();
        zeta.Lag.ShouldBe(60);
    }

    [Fact]
    public void a_store_global_agent_under_a_tenanted_store_keeps_its_own_cell()
    {
        // The marten#4761 follow-up: a single :All agent records no per-tenant rows at all. Without
        // its own cell it disappears from the report entirely.
        var lags = calculate([ShardName.Compose("Trips"), ShardName.Compose("Orders")],
            row(ShardState.HighWaterMark, 120),
            row("HighWaterMark:acme", 100),
            row("Trips:All:acme", 100),
            row("Orders:All", 60));

        var orders = lags.Where(x => x.Shard.Name == "Orders").ToArray();
        orders.Length.ShouldBe(2);

        var global = orders.Single(x => x.Shard.TenantId == null);
        global.HasProgressionRow.ShouldBeTrue();
        global.Sequence.ShouldBe(60);
        global.HighWaterMark.ShouldBe(120);

        // ...and the tenant cell for the same shard is still reported as behind
        orders.Single(x => x.Shard.TenantId == "acme").HasProgressionRow.ShouldBeFalse();

        // The per-tenant projection does NOT gain a spurious store-global cell
        lags.Count(x => x.Shard.Name == "Trips").ShouldBe(1);
    }

    [Fact]
    public void tenant_qualified_rows_under_a_store_global_mark_are_not_a_shard_with_no_row()
    {
        // jasperfx#1046: tenants are normally discovered from the high-water rows, which left a
        // per-tenant agent under a store-global mark reported as never having started
        var lags = calculate([ShardName.Compose("Trips"), ShardName.Compose("Orders", version: 2)],
            row(ShardState.HighWaterMark, 100),
            row("Trips:All:zeta", 40),
            row("Trips:All:acme", 100),
            row("Orders:V2:All:acme", 100));

        lags.Select(x => x.Shard.Identity).ShouldBe(["Trips:All:acme", "Trips:All:zeta", "Orders:V2:All:acme"]);
        lags.ShouldAllBe(x => x.HasProgressionRow && x.HighWaterMark == 100);
    }

    [Fact]
    public void a_shards_own_row_wins_over_tenant_qualified_rows_under_a_store_global_mark()
    {
        var lags = calculate([ShardName.Compose("Trips")],
            row(ShardState.HighWaterMark, 100),
            row("Trips:All", 100),
            row("Trips:All:acme", 40));

        lags.ShouldHaveSingleItem().Shard.TenantId.ShouldBeNull();
    }

    // jasperfx#1046: StaleCells is measured against the highest sequence, not the persisted mark

    private static string[] staleCells(long ceiling, IEnumerable<ShardName> shards, params ShardState[] progress)
        => ProjectionLagCalculator.StaleCells(calculate(shards, progress), ceiling)
            .Select(x => x.Shard.Identity).ToArray();

    [Fact]
    public void a_shard_ahead_of_the_ceiling_is_not_stale()
    {
        // The head can sit below a shard: events get removed, and a read can race an append
        staleCells(100, [ShardName.Compose("Trips")],
                row(ShardState.HighWaterMark, 100),
                row("Trips:All", 120))
            .ShouldBeEmpty();
    }

    [Fact]
    public void a_shard_ahead_of_the_persisted_mark_is_current_when_it_has_reached_the_ceiling()
    {
        // The mark row trails the agent, so a shard reads past it routinely
        staleCells(100, [ShardName.Compose("Trips")],
                row(ShardState.HighWaterMark, 60),
                row("Trips:All", 100))
            .ShouldBeEmpty();
    }

    [Fact]
    public void a_shard_at_a_trailing_mark_is_stale_even_though_it_reports_caught_up()
    {
        // The caller's own events are at the ceiling, above a mark the agent has not published. Measuring
        // against the mark answers "current" on exactly the question being asked.
        var lags = calculate([ShardName.Compose("Trips")],
            row(ShardState.HighWaterMark, 60),
            row("Trips:All", 60));

        lags.ShouldHaveSingleItem().IsCaughtUp.ShouldBeTrue();
        ProjectionLagCalculator.StaleCells(lags, 100).ShouldHaveSingleItem().Shard.Identity.ShouldBe("Trips:All");
    }

    [Fact]
    public void a_registered_shard_with_no_row_is_stale_beside_a_sibling_at_the_ceiling()
    {
        staleCells(100, [ShardName.Compose("Trips"), ShardName.Compose("Orders")],
                row("Trips:All", 100))
            .ShouldBe(["Orders:All"]);
    }

    [Fact]
    public void rows_the_registry_does_not_know_never_make_anything_stale()
    {
        // marten#5161: Polecat compared every row present, so a retired projection or a bookkeeping row
        // that nothing advances held the wait open until it timed out
        staleCells(100, [ShardName.Compose("Trips")],
                row("Trips:All", 100),
                row("SomeRetiredProjection:All", 1),
                row("HighWaterAllocationFence", 1))
            .ShouldBeEmpty();
    }

    [Fact]
    public void a_prior_versions_row_does_not_make_the_current_version_current()
    {
        staleCells(100, [ShardName.Compose("Trips", version: 3)], row("Trips:V2:All", 100))
            .ShouldBe(["Trips:V3:All"]);
    }

    [Fact]
    public void a_composites_version_zero_shard_is_matched_to_its_unversioned_row()
    {
        // A composite carries Version 0 and persists Name:All, which parses back as version 1, so a
        // field-by-field comparison would never match it
        staleCells(100, [new ShardName("Composite", ShardName.All, 0)], row("Composite:All", 100))
            .ShouldBeEmpty();
    }

    [Fact]
    public void a_tenant_with_fewer_events_than_the_ceiling_is_held_to_its_own_mark()
    {
        // marten#4761: held to the store-wide maximum, zeta could never finish
        staleCells(100, [ShardName.Compose("Trips")],
                row("HighWaterMark:acme", 100),
                row("HighWaterMark:zeta", 20),
                row("Trips:All:acme", 100),
                row("Trips:All:zeta", 20))
            .ShouldBeEmpty();
    }

    [Fact]
    public void a_tenant_behind_its_own_mark_is_stale()
    {
        staleCells(100, [ShardName.Compose("Trips")],
                row("HighWaterMark:acme", 100),
                row("HighWaterMark:zeta", 20),
                row("Trips:All:acme", 100),
                row("Trips:All:zeta", 5))
            .ShouldBe(["Trips:All:zeta"]);
    }

    [Fact]
    public void tenant_marks_that_have_not_reached_the_ceiling_are_not_yet_about_these_events()
    {
        staleCells(100, [ShardName.Compose("Trips")],
                row("HighWaterMark:acme", 80),
                row("Trips:All:acme", 80))
            .ShouldBe(["Trips:All:acme"]);
    }

    [Fact]
    public void a_store_global_agent_is_not_held_to_the_tenant_rows_it_does_not_own()
    {
        // The marten#4761 follow-up. A single :All agent spans every tenant, so its tenant cells have no row
        // and never will
        staleCells(100, [ShardName.Compose("Trips")],
                row("HighWaterMark:acme", 100),
                row("Trips:All", 100))
            .ShouldBeEmpty();

        staleCells(100, [ShardName.Compose("Trips")],
                row("HighWaterMark:acme", 100),
                row("Trips:All", 60))
            .ShouldBe(["Trips:All"]);
    }

    [Fact]
    public void a_tenant_qualified_row_under_a_store_global_mark_is_held_to_the_ceiling()
    {
        var shards = new[] { ShardName.Compose("Trips") };

        staleCells(100, shards, row("Trips:All:acme", 100)).ShouldBeEmpty();
        staleCells(100, shards, row("Trips:All:acme", 100), row("Trips:All:zeta", 60)).ShouldBe(["Trips:All:zeta"]);
    }

    [Fact]
    public void a_sliced_projection_reports_one_cell_per_slice()
    {
        // Flattening to (name, version, tenant) would collapse these into one indistinguishable row.
        var lags = calculate(
            [ShardName.Compose("Trips", "One"), ShardName.Compose("Trips", "Two")],
            row(ShardState.HighWaterMark, 100),
            row("Trips:One", 100),
            row("Trips:Two", 40));

        lags.Count.ShouldBe(2);
        lags.Single(x => x.Shard.ShardKey == "One").IsCaughtUp.ShouldBeTrue();
        lags.Single(x => x.Shard.ShardKey == "Two").Lag.ShouldBe(60);
    }

    [Fact]
    public void an_already_fanned_out_registry_does_not_multiply_cells()
    {
        var lags = calculate(
            [ShardName.Compose("Trips", "All", "acme"), ShardName.Compose("Trips", "All", "zeta")],
            row("HighWaterMark:acme", 100),
            row("HighWaterMark:zeta", 100),
            row("Trips:All:acme", 100),
            row("Trips:All:zeta", 100));

        lags.Count.ShouldBe(2);
    }

    [Fact]
    public void filter_by_projection_name_spans_slices_and_tenants()
    {
        var lags = calculate(
            [ShardName.Compose("Trips", "One"), ShardName.Compose("Trips", "Two"), ShardName.Compose("Orders")],
            row(ShardState.HighWaterMark, 100),
            row("Trips:One", 100),
            row("Trips:Two", 40),
            row("Orders:All", 100));

        ProjectionLagCalculator.Filter(lags, ShardName.Compose("Trips")).Count.ShouldBe(2);
        ProjectionLagCalculator.Filter(lags, ShardName.Compose("Trips", "Two"))
            .ShouldHaveSingleItem().Lag.ShouldBe(60);
    }

    [Fact]
    public void filter_by_tenant_qualified_name()
    {
        var lags = calculate([ShardName.Compose("Trips")],
            row("HighWaterMark:acme", 100),
            row("HighWaterMark:zeta", 100),
            row("Trips:All:acme", 90),
            row("Trips:All:zeta", 10));

        var filtered = ProjectionLagCalculator.Filter(lags, ShardName.Compose("Trips", "All", "zeta"));
        filtered.ShouldHaveSingleItem().Lag.ShouldBe(90);
    }

    [Fact]
    public void filter_ignores_the_version_of_the_argument()
    {
        // The registry only ever holds the current version, so a caller must not have to know it
        var lags = calculate([ShardName.Compose("Trips", version: 4)],
            row(ShardState.HighWaterMark, 100),
            row("Trips:V4:All", 100));

        ProjectionLagCalculator.Filter(lags, ShardName.Compose("Trips")).ShouldHaveSingleItem()
            .Shard.Version.ShouldBe(4u);
    }

    [Fact]
    public async Task database_default_correlates_over_one_all_projection_progress_round_trip()
    {
        var recording = new LagDatabase([
            row(ShardState.HighWaterMark, 100),
            row("HighWaterMark:acme", 80),
            row("Trips:All:acme", 30)
        ]);

        // Default interface members are reached through the interface -- which is the point:
        // a store that has not implemented anything new gets this read for free
        IEventDatabase database = recording;

        var lags = await database.FetchProjectionLagAsync([ShardName.Compose("Trips")],
            TestContext.Current.CancellationToken);

        recording.Reads.ShouldBe(1);
        var lag = lags.ShouldHaveSingleItem();
        lag.DatabaseIdentifier.ShouldBe("lag-db");
        lag.Shard.TenantId.ShouldBe("acme");
        lag.Lag.ShouldBe(50);
    }

    [Fact]
    public async Task database_default_filters_to_the_named_shard()
    {
        IEventDatabase database = new LagDatabase([
            row(ShardState.HighWaterMark, 100),
            row("Trips:All", 90),
            row("Orders:All", 10)
        ]);

        var lags = await database.FetchProjectionLagAsync(
            [ShardName.Compose("Trips"), ShardName.Compose("Orders")],
            ShardName.Compose("Orders"),
            TestContext.Current.CancellationToken);

        lags.ShouldHaveSingleItem().Lag.ShouldBe(90);
    }

    // A bare IEventDatabase that overrides nothing but the progression read, so the calls above
    // exercise the default interface implementations added for jasperfx#619
    private sealed class LagDatabase : IEventDatabase
    {
        private readonly IReadOnlyList<ShardState> _progress;

        public LagDatabase(IReadOnlyList<ShardState> progress) => _progress = progress;

        public int Reads { get; private set; }

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
        {
            Reads++;
            return Task.FromResult(_progress);
        }

        public string Identifier => "lag-db";
        public Uri DatabaseUri => throw new NotImplementedException();
        public ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => throw new NotImplementedException();

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
            => throw new NotImplementedException();
    }
}
