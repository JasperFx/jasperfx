using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Event data query events

public record BargeLoaded(string Cargo, int Tonnes);

/// <summary>
/// The same shape as <see cref="BargeLoaded"/> under another event type: the decoy for a store that
/// matches on the body's shape rather than on the stored event type.
/// </summary>
public record BargeUnloaded(string Cargo, int Tonnes);

#endregion

/// <summary>
/// <c>IReadOnlyEventStore.QueryEventData&lt;T&gt;()</c> — the bodies of one event type, across every
/// stream, as an <see cref="IQueryable{T}"/> executed through the shared
/// <see cref="DocumentQueryableExtensions"/> terminators. See jasperfx#1073.
/// </summary>
/// <remarks>
/// <para>
/// The <c>StreamStateQueryCompliance</c> discipline: every predicate fact asserts exact expected
/// membership against seeded data with decoys that fail only that predicate, because a silently
/// dropped <c>Where</c> returns every event and reads as filtered. The standing decoy is
/// <see cref="BargeUnloaded"/>, identical in shape to <see cref="BargeLoaded"/>: a store that queries
/// bodies without narrowing to the event type returns it.
/// </para>
/// <para>
/// Not a LINQ conformance suite. Pinned is the minimum the contract promises — <c>Where</c> over the
/// body's members, <c>OrderBy</c>, <c>Skip</c>/<c>Take</c>, and the async terminators. The
/// tenant-scoped overload is not exercised here, as it is not for <c>QueryStreamStates</c>.
/// </para>
/// </remarks>
public abstract class EventDataQueryCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_event_data_query";

        config.AddEventType<BargeLoaded>();
        config.AddEventType<BargeUnloaded>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private IQueryable<BargeLoaded> Loads
        => theFixture.EventStore.OpenReadOnlyEventStore().QueryEventData<BargeLoaded>();

    /// <summary>One stream per call, holding <paramref name="events"/>, in one save.</summary>
    private async Task<Guid> aStreamAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task appendAsync(Guid streamId, params object[] events)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(streamId, events);
        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task only_events_of_the_requested_type_across_every_stream()
    {
        var first = await aStreamAsync(new BargeLoaded("Coal", 500), new BargeUnloaded("Coal", 500));
        await aStreamAsync(new BargeLoaded("Grain", 200));
        await appendAsync(first, new BargeLoaded("Ore", 900));

        var loads = await Loads.ToListAsync(Cancellation);

        loads.Count.ShouldBe(3);
        loads.Select(x => x.Cargo).OrderBy(x => x).ShouldBe(["Coal", "Grain", "Ore"]);
    }

    [Fact]
    public async Task where_on_a_numeric_member()
    {
        await aStreamAsync(new BargeLoaded("Coal", 999), new BargeLoaded("Grain", 1000), new BargeLoaded("Ore", 2500));

        // The decoy matches the predicate in every way but its event type.
        await aStreamAsync(new BargeUnloaded("Steel", 5000));

        var matched = await Loads.Where(x => x.Tonnes >= 1000).ToListAsync(Cancellation);

        // Inclusive at the bound: the 1000-tonne load is the one an exclusive comparison loses.
        matched.Select(x => x.Cargo).OrderBy(x => x).ShouldBe(["Grain", "Ore"]);
    }

    [Fact]
    public async Task where_on_a_string_member()
    {
        await aStreamAsync(new BargeLoaded("Coal", 100), new BargeLoaded("Grain", 200));
        await aStreamAsync(new BargeLoaded("Coal", 300), new BargeUnloaded("Coal", 400));

        var matched = await Loads.Where(x => x.Cargo == "Coal").ToListAsync(Cancellation);

        matched.Select(x => x.Tonnes).OrderBy(x => x).ShouldBe([100, 300]);
    }

    [Fact]
    public async Task a_predicate_over_several_members()
    {
        await aStreamAsync(
            new BargeLoaded("Coal", 2000),   // match
            new BargeLoaded("Coal", 10),     // fails tonnes
            new BargeLoaded("Grain", 2000),  // fails cargo
            new BargeUnloaded("Coal", 2000)); // fails type

        var matched = await Loads.Where(x => x.Cargo == "Coal" && x.Tonnes > 1000).ToListAsync(Cancellation);

        matched.ShouldHaveSingleItem().ShouldBe(new BargeLoaded("Coal", 2000));
    }

    [Fact]
    public async Task ordering_and_paging_over_a_body_member()
    {
        await aStreamAsync(new BargeLoaded("A", 50), new BargeLoaded("B", 10), new BargeLoaded("C", 40));
        await aStreamAsync(new BargeLoaded("D", 30), new BargeLoaded("E", 20), new BargeUnloaded("Z", 1));

        var ordered = Loads.OrderBy(x => x.Tonnes);

        (await ordered.Skip(0).Take(2).ToListAsync(Cancellation)).Select(x => x.Cargo).ShouldBe(["B", "E"]);
        (await ordered.Skip(2).Take(2).ToListAsync(Cancellation)).Select(x => x.Cargo).ShouldBe(["D", "C"]);
        (await ordered.Skip(4).Take(2).ToListAsync(Cancellation)).Select(x => x.Cargo).ShouldBe(["A"]);
        (await ordered.Skip(20).Take(2).ToListAsync(Cancellation)).ShouldBeEmpty();

        (await Loads.OrderByDescending(x => x.Tonnes).ToListAsync(Cancellation)).First().Cargo.ShouldBe("A");
    }

    [Fact]
    public async Task the_shared_terminators_dispatch_against_this_queryable()
    {
        await aStreamAsync(new BargeLoaded("Coal", 100), new BargeLoaded("Ore", 3000), new BargeUnloaded("Ore", 3000));

        (await Loads.CountAsync(Cancellation)).ShouldBe(2);
        (await Loads.CountAsync(x => x.Tonnes > 1000, Cancellation)).ShouldBe(1);
        (await Loads.AnyAsync(Cancellation)).ShouldBeTrue();
        (await Loads.AnyAsync(x => x.Cargo == "Steel", Cancellation)).ShouldBeFalse();

        var heaviest = await Loads.OrderByDescending(x => x.Tonnes).FirstOrDefaultAsync(Cancellation);
        heaviest.ShouldBe(new BargeLoaded("Ore", 3000));
    }

    /// <summary>
    /// A console builds its query a clause at a time, held in a local, so the queryable has to compose
    /// across statements.
    /// </summary>
    [Fact]
    public async Task a_queryable_composes_across_statements()
    {
        await aStreamAsync(
            new BargeLoaded("Coal", 2000),
            new BargeLoaded("Coal", 5),      // dropped by the second clause
            new BargeLoaded("Grain", 2000)); // dropped by the first

        var query = Loads;
        query = query.Where(x => x.Cargo == "Coal");
        query = query.Where(x => x.Tonnes > 100);

        (await query.ToListAsync(Cancellation)).ShouldHaveSingleItem().Tonnes.ShouldBe(2000);
    }

    [Fact]
    public async Task an_empty_answer_when_nothing_matches()
    {
        await aStreamAsync(new BargeLoaded("Coal", 100), new BargeUnloaded("Steel", 9000));

        var query = Loads.Where(x => x.Tonnes > 1000);

        (await query.ToListAsync(Cancellation)).ShouldBeEmpty();
        (await query.CountAsync(Cancellation)).ShouldBe(0);
        (await query.AnyAsync(Cancellation)).ShouldBeFalse();
        (await query.FirstOrDefaultAsync(Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task an_event_type_with_no_events_is_an_empty_answer()
    {
        await aStreamAsync(new BargeUnloaded("Steel", 9000));

        (await Loads.ToListAsync(Cancellation)).ShouldBeEmpty();
        (await Loads.CountAsync(Cancellation)).ShouldBe(0);
    }
}
