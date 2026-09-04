using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Stream state query events and aggregates

public record FreighterLaunched(string Name);

public record FreighterDocked(string Port);

public record FreighterScrapped;

public partial class ComplianceFreighter
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Dockings { get; set; }

    public static ComplianceFreighter Create(FreighterLaunched e) => new() { Name = e.Name };

    public void Apply(FreighterDocked _) => Dockings++;

    public void Apply(FreighterScrapped _)
    {
    }
}

/// <summary>
/// A second aggregate type over the same events, so the aggregate-type facts have a decoy that
/// differs ONLY in type.
/// </summary>
public partial class ComplianceTugboat
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Dockings { get; set; }

    public static ComplianceTugboat Create(FreighterLaunched e) => new() { Name = e.Name };

    public void Apply(FreighterDocked _) => Dockings++;
}

#endregion

/// <summary>
/// <c>IReadOnlyEventStore.QueryStreamStates()</c> — the streams table as an
/// <see cref="IQueryable{T}"/> of <see cref="StreamState"/>, executed through the shared
/// <see cref="DocumentQueryableExtensions"/> terminators. See jasperfx#740.
/// </summary>
/// <remarks>
/// <para>
/// Same discipline as <c>EventQueryCompliance</c>, and for the same reason: every predicate fact
/// asserts exact expected membership against seeded data with decoys that fail only that
/// predicate — never merely that the call succeeds — because a silently-dropped <c>Where</c>
/// clause returns unfiltered streams that read as filtered. One fact per public get member of
/// <see cref="StreamState"/>, including the jasperfx#740 <see cref="StreamState.CompactedVersion"/>
/// watermark and the <c>x.AggregateType == typeof(X)</c> form, which is exactly the Stream
/// Compaction Policy's selector.
/// </para>
/// <para>
/// Like <c>DocumentQueryCompliance</c>, this is not a LINQ conformance suite: what is pinned is
/// the minimum translatable set the contract promises — <c>Where</c> over every member,
/// <c>OrderBy</c>/<c>ThenBy</c>, <c>Skip</c>/<c>Take</c>, and the async terminators. A member a
/// provider cannot translate must fail the query naming that member, never silently match all
/// rows; that refusal shape cannot be pinned here (both current stores translate everything), so
/// it lives in the contract's xml-doc and each store's own tests.
/// </para>
/// <para>
/// The tenant-scoped overload (<c>QueryStreamStates(tenantId)</c>) is deliberately not exercised
/// here — it belongs with <c>ConjoinedEventTenancyCompliance</c>, which owns the tenant-scoped
/// store configuration, and it has a fact there.
/// </para>
/// </remarks>
public abstract class StreamStateQueryCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_stream_query";

        config.AddEventType<FreighterLaunched>();
        config.AddEventType<FreighterDocked>();
        config.AddEventType<FreighterScrapped>();

        // Inline snapshots so both aggregate types are registered store-side: the
        // AggregateType == typeof(X) translation resolves the typeof constant against the store's
        // known aggregate identities.
        config.Snapshot<ComplianceFreighter>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceTugboat>(SnapshotLifecycle.Inline);
    };

    /// <summary>
    /// Stream identity is a store-level setting, so the <see cref="StreamState.Key"/> fact needs
    /// its own string-identified store — the same split as <c>StreamCompactingCompliance</c>.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_stream_query_string";
        config.StreamIdentity = StreamIdentity.AsString;

        config.AddEventType<FreighterLaunched>();
        config.AddEventType<FreighterDocked>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private IQueryable<StreamState> Streams
        => theFixture.EventStore.OpenReadOnlyEventStore().QueryStreamStates();

    /// <summary>
    /// Start a freighter stream with a launch plus <paramref name="docks"/> docking events, one
    /// save, and return its id.
    /// </summary>
    private async Task<Guid> aFreighterAsync(int docks = 0)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = new object[] { new FreighterLaunched("Cargolux") }
            .Concat(Enumerable.Range(0, docks).Select(object (i) => new FreighterDocked($"Port {i}")))
            .ToArray();
        EventsFor(session).StartStream<ComplianceFreighter>(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<Guid> aTugboatAsync(int docks = 0)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = new object[] { new FreighterLaunched("Pushy") }
            .Concat(Enumerable.Range(0, docks).Select(object (i) => new FreighterDocked($"Port {i}")))
            .ToArray();
        EventsFor(session).StartStream<ComplianceTugboat>(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task appendDockingAsync(Guid streamId)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(streamId, new FreighterDocked("Later port"));
        await SaveChangesAsync(session);
    }

    private async Task archiveAsync(Guid streamId)
    {
        await using var session = OpenSession();
        EventsFor(session).ArchiveStream(streamId);
        await SaveChangesAsync(session);
    }

    private async Task compactAsync(Guid streamId, long? throughVersion = null)
    {
        await using var session = OpenSession();
        await EventsFor(session).CompactStreamAsync<ComplianceFreighter>(streamId,
            throughVersion is { } v ? x => x.Version = v : null);
        await SaveChangesAsync(session);
    }

    // ---- one Where() fact per public get member ----

    [Fact]
    public async Task where_on_id()
    {
        await aFreighterAsync();
        var target = await aFreighterAsync();
        await aFreighterAsync();

        (await Streams.CountAsync(Cancellation)).ShouldBe(3);

        var matched = await Streams.Where(x => x.Id == target).ToListAsync(Cancellation);

        matched.ShouldHaveSingleItem().Id.ShouldBe(target);
    }

    [Fact]
    public async Task where_on_key_for_a_string_identified_store()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);
        await theFixture.CleanEventDataAsync();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream("freighter-a", new FreighterLaunched("A"));
            EventsFor(session).StartStream("freighter-b", new FreighterLaunched("B"), new FreighterDocked("Kiel"));
            await SaveChangesAsync(session);
        }

        var matched = await Streams.Where(x => x.Key == "freighter-b").ToListAsync(Cancellation);

        var state = matched.ShouldHaveSingleItem();
        state.Key.ShouldBe("freighter-b");
        state.Version.ShouldBe(2);
    }

    [Fact]
    public async Task where_on_version()
    {
        await aFreighterAsync(docks: 0);                       // version 1
        var three = await aFreighterAsync(docks: 2);           // version 3
        var five = await aFreighterAsync(docks: 4);            // version 5

        var matched = await Streams.Where(x => x.Version >= 3).ToListAsync(Cancellation);

        // Inclusive at the bound: the version-3 stream is the one an exclusive comparison loses.
        matched.Count.ShouldBe(2);
        matched.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { three, five }.OrderBy(x => x));
    }

    /// <summary>
    /// The compaction policy's selector, in exactly the form CritterWatch will issue: equality
    /// against a <c>typeof</c> constant, translated to the stored aggregate-type identity.
    /// </summary>
    [Fact]
    public async Task where_on_aggregate_type_typeof_equality()
    {
        var first = await aFreighterAsync();
        var second = await aFreighterAsync();
        await aTugboatAsync();

        // A stream started with no aggregate type at all: the null-cell decoy.
        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(Guid.NewGuid(), new FreighterLaunched("Untyped"));
            await SaveChangesAsync(session);
        }

        var matched = await Streams
            .Where(x => x.AggregateType == typeof(ComplianceFreighter))
            .ToListAsync(Cancellation);

        matched.Count.ShouldBe(2);
        matched.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { first, second }.OrderBy(x => x));
        matched.ShouldAllBe(x => x.AggregateType == typeof(ComplianceFreighter));
    }

    [Fact]
    public async Task where_on_created()
    {
        var early = await aFreighterAsync();
        await Task.Delay(40, Cancellation);
        var late = await aFreighterAsync();

        // Appended AFTER the late stream was created, so the early stream's LastTimestamp is the
        // newest in the store while its Created stays the oldest — the trap for a provider mapping
        // Created onto the wrong column.
        await appendDockingAsync(early);

        var states = await Streams.ToListAsync(Cancellation);
        var lateCreated = states.Single(x => x.Id == late).Created;

        var matched = await Streams.Where(x => x.Created >= lateCreated).ToListAsync(Cancellation);

        matched.ShouldHaveSingleItem().Id.ShouldBe(late);
    }

    [Fact]
    public async Task where_on_last_timestamp()
    {
        var active = await aFreighterAsync();
        await Task.Delay(40, Cancellation);
        var idle = await aFreighterAsync();
        await Task.Delay(40, Cancellation);
        await appendDockingAsync(active);

        var states = await Streams.ToListAsync(Cancellation);
        var activeLast = states.Single(x => x.Id == active).LastTimestamp;

        var matched = await Streams.Where(x => x.LastTimestamp >= activeLast).ToListAsync(Cancellation);

        // The OLDER stream by creation is the one that matches, because it was appended to last —
        // a provider reading Created here returns the idle stream instead.
        matched.ShouldHaveSingleItem().Id.ShouldBe(active);
        matched.Single().Id.ShouldNotBe(idle);
    }

    [Fact]
    public async Task where_on_is_archived_in_both_directions()
    {
        var liveOne = await aFreighterAsync();
        var liveTwo = await aFreighterAsync();
        var archived = await aFreighterAsync();
        await archiveAsync(archived);

        var archivedOnly = await Streams.Where(x => x.IsArchived).ToListAsync(Cancellation);
        archivedOnly.ShouldHaveSingleItem().Id.ShouldBe(archived);

        var liveOnly = await Streams.Where(x => !x.IsArchived).ToListAsync(Cancellation);
        liveOnly.Count.ShouldBe(2);
        liveOnly.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { liveOne, liveTwo }.OrderBy(x => x));
    }

    [Fact]
    public async Task where_on_compacted_version()
    {
        var compacted = await aFreighterAsync(docks: 8);       // version 9
        await compactAsync(compacted, throughVersion: 5);
        var untouched = await aFreighterAsync(docks: 8);       // version 9, never compacted

        // Equality in both directions, so the watermark has to be surfaced exactly — a store
        // hardcoding zero passes one half and fails the other.
        var atFive = await Streams.Where(x => x.CompactedVersion == 5).ToListAsync(Cancellation);
        atFive.ShouldHaveSingleItem().Id.ShouldBe(compacted);

        var never = await Streams.Where(x => x.CompactedVersion == 0).ToListAsync(Cancellation);
        never.ShouldHaveSingleItem().Id.ShouldBe(untouched);
    }

    /// <summary>
    /// The watermark on the fetch path agrees with the queryable: <c>FetchStreamStateAsync</c>
    /// reports the same <see cref="StreamState.CompactedVersion"/>, and a full compaction sets the
    /// watermark to the stream's version so the un-compacted growth reads zero.
    /// </summary>
    [Fact]
    public async Task fetch_stream_state_carries_the_compaction_watermark()
    {
        var partial = await aFreighterAsync(docks: 8);         // version 9
        await compactAsync(partial, throughVersion: 5);

        var full = await aFreighterAsync(docks: 8);            // version 9
        await compactAsync(full);

        var fresh = await aFreighterAsync();

        await using var session = OpenSession();

        var partialState = await EventsFor(session).FetchStreamStateAsync(partial, Cancellation);
        partialState.ShouldNotBeNull();
        partialState.Version.ShouldBe(9);
        partialState.CompactedVersion.ShouldBe(5);
        (partialState.Version - partialState.CompactedVersion).ShouldBe(4);

        var fullState = await EventsFor(session).FetchStreamStateAsync(full, Cancellation);
        fullState.ShouldNotBeNull();
        fullState.CompactedVersion.ShouldBe(9);
        (fullState.Version - fullState.CompactedVersion).ShouldBe(0);

        var freshState = await EventsFor(session).FetchStreamStateAsync(fresh, Cancellation);
        freshState.ShouldNotBeNull();
        freshState.CompactedVersion.ShouldBe(0);
    }

    // ---- combinations ----

    /// <summary>
    /// The Stream Compaction Policy's own predicate, verbatim: aggregate type AND un-compacted
    /// growth above a threshold AND not archived. Two matchers and four decoys, each decoy failing
    /// exactly one conjunct.
    /// </summary>
    [Fact]
    public async Task the_compaction_policy_shape()
    {
        var overgrown = await aFreighterAsync(docks: 8);       // version 9
        await compactAsync(overgrown, throughVersion: 5);      // growth 4 -> match

        var neverCompacted = await aFreighterAsync(docks: 8);  // growth 9 -> match

        var freshlyCompacted = await aFreighterAsync(docks: 8);
        await compactAsync(freshlyCompacted);                  // growth 0 -> fails growth (raw Version 9!)

        await aFreighterAsync(docks: 2);                       // growth 3, at threshold -> fails growth

        await aTugboatAsync(docks: 8);                         // growth 9 -> fails type

        var mothballed = await aFreighterAsync(docks: 8);      // growth 9 -> fails archived
        await archiveAsync(mothballed);

        var matched = await Streams
            .Where(x => x.AggregateType == typeof(ComplianceFreighter)
                        && x.Version - x.CompactedVersion > 3
                        && !x.IsArchived)
            .ToListAsync(Cancellation);

        // The freshly-compacted stream is the load-bearing decoy: its raw Version is 9, so a store
        // thresholding on Version instead of growth includes it and fails here. That distinction is
        // the entire reason the watermark exists.
        matched.Count.ShouldBe(2);
        matched.Select(x => x.Id).OrderBy(x => x)
            .ShouldBe(new[] { overgrown, neverCompacted }.OrderBy(x => x));
    }

    [Fact]
    public async Task version_bound_combines_with_aggregate_type()
    {
        var bigFreighter = await aFreighterAsync(docks: 4);    // version 5
        await aFreighterAsync();                               // version 1 -> fails version
        await aTugboatAsync(docks: 4);                         // version 5 -> fails type

        var matched = await Streams
            .Where(x => x.AggregateType == typeof(ComplianceFreighter) && x.Version >= 3)
            .ToListAsync(Cancellation);

        matched.ShouldHaveSingleItem().Id.ShouldBe(bigFreighter);
    }

    // ---- ordering, paging, terminators ----

    /// <summary>
    /// The stated ordering for deterministic pages — the one the <c>stream-query</c> command uses:
    /// creation order (oldest first), ties broken by stream identity. Pinned here as
    /// <c>OrderBy(Created).ThenBy(Id)</c> + <c>Skip</c>/<c>Take</c>: pages are disjoint, complete,
    /// and ordered across page boundaries, and <c>CountAsync</c> over the same filter reports the
    /// total the pages walk.
    /// </summary>
    [Fact]
    public async Task paging_walks_the_stated_ordering()
    {
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await aFreighterAsync());
            await Task.Delay(25, Cancellation);
        }

        var ordered = StreamQueryOrdered();

        (await Streams.CountAsync(Cancellation)).ShouldBe(5);

        var pages = new List<IReadOnlyList<StreamState>>();
        for (var page = 0; page < 3; page++)
        {
            pages.Add(await ordered.Skip(page * 2).Take(2).ToListAsync(Cancellation));
        }

        pages[0].Count.ShouldBe(2);
        pages[1].Count.ShouldBe(2);
        pages[2].Count.ShouldBe(1);

        // Disjoint, complete, and in creation order across the page boundaries.
        pages.SelectMany(x => x).Select(x => x.Id).ShouldBe(created);

        // A page past the end is empty, while the count stays truthful.
        (await ordered.Skip(20).Take(2).ToListAsync(Cancellation)).ShouldBeEmpty();
        (await Streams.CountAsync(Cancellation)).ShouldBe(5);

        IOrderedQueryable<StreamState> StreamQueryOrdered()
            => Streams.OrderBy(x => x.Created).ThenBy(x => x.Id);
    }

    [Fact]
    public async Task the_shared_terminators_dispatch_against_this_queryable()
    {
        var oldest = await aFreighterAsync(docks: 4);
        await Task.Delay(25, Cancellation);
        await aFreighterAsync();

        // Every shared terminator, on the events-side queryable: the whole point of reusing the
        // document execution hook is that none of these needed anything new.
        (await Streams.CountAsync(x => x.Version >= 3, Cancellation)).ShouldBe(1);
        (await Streams.AnyAsync(Cancellation)).ShouldBeTrue();
        (await Streams.AnyAsync(x => x.IsArchived, Cancellation)).ShouldBeFalse();

        var first = await Streams.OrderBy(x => x.Created).FirstOrDefaultAsync(Cancellation);
        first.ShouldNotBeNull();
        first.Id.ShouldBe(oldest);
    }

    /// <summary>
    /// The shape fact, mirroring <c>DocumentQueryCompliance</c>: a real <see cref="IQueryable{T}"/>
    /// composes across statements, because real consumer code — the compaction policy evaluator
    /// included — adds clauses conditionally to a query held in a local.
    /// </summary>
    [Fact]
    public async Task a_queryable_composes_across_statements()
    {
        var firstBig = await aFreighterAsync(docks: 4);        // version 5, live
        var secondBig = await aFreighterAsync(docks: 4);       // version 5, live
        await aFreighterAsync();                               // version 1 -> dropped by the first clause

        var archived = await aFreighterAsync(docks: 4);        // version 5 -> dropped by the second
        await archiveAsync(archived);

        var query = Streams;
        query = query.Where(x => x.Version >= 3);
        query = query.Where(x => !x.IsArchived);

        var matched = await query.ToListAsync(Cancellation);

        matched.Count.ShouldBe(2);
        matched.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { firstBig, secondBig }.OrderBy(x => x));
    }

    // ---- truthful zeros ----

    [Fact]
    public async Task every_predicate_returns_an_empty_answer_when_nothing_matches()
    {
        await aFreighterAsync(docks: 2);

        var nonMatching = new (string Name, IQueryable<StreamState> Query)[]
        {
            ("Id", Streams.Where(x => x.Id == Guid.NewGuid())),
            ("Version", Streams.Where(x => x.Version > 1000)),
            ("AggregateType", Streams.Where(x => x.AggregateType == typeof(ComplianceTugboat))),
            ("IsArchived", Streams.Where(x => x.IsArchived)),
            ("CompactedVersion", Streams.Where(x => x.CompactedVersion > 0)),
            ("Created", Streams.Where(x => x.Created > DateTimeOffset.UtcNow.AddYears(10))),
            ("LastTimestamp", Streams.Where(x => x.LastTimestamp > DateTimeOffset.UtcNow.AddYears(10)))
        };

        foreach (var (name, query) in nonMatching)
        {
            (await query.ToListAsync(Cancellation)).ShouldBeEmpty($"the {name} predicate should have matched nothing");
            (await query.CountAsync(Cancellation)).ShouldBe(0);
            (await query.AnyAsync(Cancellation)).ShouldBeFalse();
        }
    }
}