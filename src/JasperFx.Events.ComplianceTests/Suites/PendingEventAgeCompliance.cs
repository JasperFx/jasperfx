using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Daemon;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Pending event age events

public record LanternHung(string Hill);

public record LanternTrimmed(string Keeper);

#endregion

/// <summary>
/// <see cref="IEventDatabase.FetchFirstEventTimestampsAfterAsync" /> (jasperfx#1150): for each position, the
/// timestamp of the first event after it, which is the age of the oldest event a projection shard has not
/// processed yet. Monitoring reports projection lag in time from it.
/// </summary>
/// <remarks>
/// <para>
/// Timestamps are compared against the events read back from the store, never against a clock in the test,
/// so a store is free to stamp them however it does. Facts that need distinct timestamps commit each event in
/// its own session with a short pause between.
/// </para>
/// <para>
/// What this does not cover: per-tenant partitioned events, which no compliance configuration can switch on
/// yet. A store with that mode should cover it in its own tests, where each tenant draws its own sequence.
/// </para>
/// </remarks>
[RequiresCapability("SupportsPendingEventAges")]
public abstract class PendingEventAgeCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    /// <summary>
    /// Far apart enough that every store's timestamp precision tells two events apart, and well past the
    /// tolerance the comparisons allow.
    /// </summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_pending_age";
        config.AddEventType<LanternHung>();
        config.AddEventType<LanternTrimmed>();
    };

    private static readonly Action<ComplianceStoreConfig> _conjoinedConfiguration = config =>
    {
        config.SchemaName = "compliance_pending_age_conjoined";
        config.ConjoinedEventTenancy = true;
        config.AddEventType<LanternHung>();
        config.AddEventType<LanternTrimmed>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<IEventDatabase> DatabaseAsync()
    {
        Assert.SkipUnless(theFixture.SupportsPendingEventAges,
            "This event store does not implement IEventDatabase.FetchFirstEventTimestampsAfterAsync (jasperfx#1150).");
        return (await EventStore.AllDatabases()).First();
    }

    private Task<IReadOnlyList<PendingEventAge>> AgesAsync(IEventDatabase database,
        params (string? TenantId, long Sequence)[] positions)
        => database.FetchFirstEventTimestampsAfterAsync(positions, Cancellation);

    /// <summary>
    /// Start a stream in its own session and read its events back from the store, so the sequences and
    /// timestamps are the stored ones.
    /// </summary>
    private async Task<IReadOnlyList<IEvent>> StartAsync(Guid streamId, string? tenantId, params object[] events)
    {
        await using (var session = await OpenAsync(tenantId))
        {
            EventsFor(session).StartStream(streamId, events);
            await SaveChangesAsync(session);
        }

        await using var query = await OpenAsync(tenantId);
        return await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
    }

    private async Task<IEvent> AppendAsync(Guid streamId, string? tenantId, object @event)
    {
        await using (var session = await OpenAsync(tenantId))
        {
            EventsFor(session).Append(streamId, @event);
            await SaveChangesAsync(session);
        }

        await using var query = await OpenAsync(tenantId);
        return (await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation))[^1];
    }

    private async Task<TOperations> OpenAsync(string? tenantId)
    {
        if (tenantId == null) return OpenSession();

        var store = (IEventStore<TOperations, TQuerySession>)EventStore;
        return store.OpenSession((await EventStore.AllDatabases()).First(), tenantId);
    }

    /// <summary>Three events on one stream, each committed on its own so their timestamps differ.</summary>
    private async Task<IEvent[]> ThreeSpacedEventsAsync()
    {
        var streamId = Guid.NewGuid();
        var first = (await StartAsync(streamId, null, new LanternHung("Amon Din"))).Single();
        await Task.Delay(Pause, Cancellation);
        var second = await AppendAsync(streamId, null, new LanternTrimmed("Hirgon"));
        await Task.Delay(Pause, Cancellation);
        var third = await AppendAsync(streamId, null, new LanternTrimmed("Beregond"));

        // The facts below are only meaningful when the store told the three apart
        second.Timestamp.ShouldBeGreaterThan(first.Timestamp + Tolerance);
        third.Timestamp.ShouldBeGreaterThan(second.Timestamp + Tolerance);

        return [first, second, third];
    }

    private static void ShouldBeTheTimestampOf(PendingEventAge age, IEvent expected)
    {
        age.Timestamp.ShouldNotBeNull($"Expected the timestamp of event #{expected.Sequence}, but the answer was 'caught up'");
        age.Timestamp!.Value.ShouldBe(expected.Timestamp, Tolerance);
        age.IsCaughtUp.ShouldBeFalse();
    }

    [Fact]
    public async Task asking_about_no_positions_returns_no_answers()
    {
        var database = await DatabaseAsync();

        (await AgesAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_empty_store_is_caught_up()
    {
        var database = await DatabaseAsync();

        var age = (await AgesAsync(database, (null, 0))).ShouldHaveSingleItem();

        age.Timestamp.ShouldBeNull();
        age.IsCaughtUp.ShouldBeTrue();
    }

    [Fact]
    public async Task a_position_at_the_head_is_caught_up()
    {
        var database = await DatabaseAsync();
        var events = await ThreeSpacedEventsAsync();

        var age = (await AgesAsync(database, (null, events[2].Sequence))).ShouldHaveSingleItem();

        age.Timestamp.ShouldBeNull();
    }

    [Fact]
    public async Task a_position_past_the_head_is_caught_up()
    {
        var database = await DatabaseAsync();
        var events = await ThreeSpacedEventsAsync();

        (await AgesAsync(database, (null, events[2].Sequence + 1000))).ShouldHaveSingleItem()
            .Timestamp.ShouldBeNull();
    }

    [Fact]
    public async Task the_answer_is_the_timestamp_of_the_first_event_after_the_position()
    {
        var database = await DatabaseAsync();
        var events = await ThreeSpacedEventsAsync();

        // Strictly after: the event AT the position has been processed, the next one has not
        ShouldBeTheTimestampOf((await AgesAsync(database, (null, events[0].Sequence))).Single(), events[1]);
        ShouldBeTheTimestampOf((await AgesAsync(database, (null, events[1].Sequence))).Single(), events[2]);
    }

    [Fact]
    public async Task a_shard_that_has_processed_nothing_waits_on_the_first_event()
    {
        var database = await DatabaseAsync();
        var events = await ThreeSpacedEventsAsync();

        ShouldBeTheTimestampOf((await AgesAsync(database, (null, 0))).Single(), events[0]);
    }

    [Fact]
    public async Task answers_come_back_one_per_position_in_the_order_asked()
    {
        var database = await DatabaseAsync();
        var events = await ThreeSpacedEventsAsync();

        var ages = await AgesAsync(database,
            (null, events[2].Sequence),
            (null, events[0].Sequence),
            (null, 0),
            (null, events[0].Sequence));

        ages.Select(x => x.Sequence).ShouldBe([events[2].Sequence, events[0].Sequence, 0, events[0].Sequence]);
        ages.ShouldAllBe(x => x.TenantId == null);

        ages[0].Timestamp.ShouldBeNull();
        ShouldBeTheTimestampOf(ages[1], events[1]);
        ShouldBeTheTimestampOf(ages[2], events[0]);
        ShouldBeTheTimestampOf(ages[3], events[1]);
    }

    [Fact]
    public async Task events_on_other_streams_count()
    {
        // A shard waits on the next event in the database, whichever stream it is on
        var database = await DatabaseAsync();
        var first = (await StartAsync(Guid.NewGuid(), null, new LanternHung("Eilenach"))).Single();
        await Task.Delay(Pause, Cancellation);
        var other = (await StartAsync(Guid.NewGuid(), null, new LanternHung("Nardol"))).Single();

        ShouldBeTheTimestampOf((await AgesAsync(database, (null, first.Sequence))).Single(), other);
    }

    [Fact]
    public async Task the_single_position_overload_gives_the_same_answer()
    {
        var database = await DatabaseAsync();
        var events = await ThreeSpacedEventsAsync();

        (await database.FetchFirstEventTimestampAfterAsync(events[0].Sequence, null, Cancellation))
            .ShouldNotBeNull().ShouldBe(events[1].Timestamp, Tolerance);
        (await database.FetchFirstEventTimestampAfterAsync(events[2].Sequence, null, Cancellation))
            .ShouldBeNull();
    }

    [Fact]
    public async Task archived_events_do_not_count()
    {
        // The daemon skips archived events, so none can be waiting on a shard
        var database = await DatabaseAsync();
        Assert.SkipUnless(theFixture.SupportsStreamArchiving, "This event store does not archive streams.");

        var archivedId = Guid.NewGuid();
        var archived = await StartAsync(archivedId, null, new LanternHung("Erelas"), new LanternTrimmed("Ingold"));
        await Task.Delay(Pause, Cancellation);
        var live = (await StartAsync(Guid.NewGuid(), null, new LanternHung("Calenhad"))).Single();

        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(archivedId);
            await SaveChangesAsync(session);
        }

        ShouldBeTheTimestampOf((await AgesAsync(database, (null, 0))).Single(), live);
        ShouldBeTheTimestampOf((await AgesAsync(database, (null, archived[0].Sequence))).Single(), live);
    }

    [Fact]
    public async Task a_shard_whose_only_remaining_events_are_archived_is_caught_up()
    {
        var database = await DatabaseAsync();
        Assert.SkipUnless(theFixture.SupportsStreamArchiving, "This event store does not archive streams.");

        var live = (await StartAsync(Guid.NewGuid(), null, new LanternHung("Min-Rimmon"))).Single();
        var archivedId = Guid.NewGuid();
        await StartAsync(archivedId, null, new LanternHung("Halifirien"));

        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(archivedId);
            await SaveChangesAsync(session);
        }

        (await AgesAsync(database, (null, live.Sequence))).Single().Timestamp.ShouldBeNull();
    }

    [Fact]
    public async Task a_tenant_waits_only_on_its_own_events()
    {
        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store does not support conjoined event tenancy.");
        await theFixture.ConfigureAsync(_conjoinedConfiguration);
        await theFixture.CleanEventDataAsync();
        var database = await DatabaseAsync();

        // Interleaved: acme, globex, acme
        var acmeFirst = (await StartAsync(Guid.NewGuid(), TenantA, new LanternHung("Amon Din"))).Single();
        await Task.Delay(Pause, Cancellation);
        var globex = (await StartAsync(Guid.NewGuid(), TenantB, new LanternHung("Eilenach"))).Single();
        await Task.Delay(Pause, Cancellation);
        var acmeSecond = (await StartAsync(Guid.NewGuid(), TenantA, new LanternHung("Nardol"))).Single();

        var ages = await AgesAsync(database,
            (TenantA, 0),
            (TenantB, 0),
            (TenantA, acmeFirst.Sequence),
            (TenantB, globex.Sequence),
            (null, acmeFirst.Sequence));

        ages.Select(x => x.TenantId).ShouldBe([TenantA, TenantB, TenantA, TenantB, null]);

        ShouldBeTheTimestampOf(ages[0], acmeFirst);
        ShouldBeTheTimestampOf(ages[1], globex);

        // Skips globex's event, which lies between acme's two
        ShouldBeTheTimestampOf(ages[2], acmeSecond);

        // acme's later event does not keep globex behind
        ages[3].Timestamp.ShouldBeNull();

        // No tenant: every tenant's events count
        ShouldBeTheTimestampOf(ages[4], globex);
    }
}
