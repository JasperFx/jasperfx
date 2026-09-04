using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Tags;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Event query events and tag

public record CargoLoaded(string Cargo);

public record CargoInspected(string Inspector);

public record CargoUnloaded(string Port);

/// <summary>
/// Strong-typed tag identity for the folded tag-condition filter.
/// </summary>
public record ManifestId(Guid Value);

#endregion

/// <summary>
/// The cross-stream query surface — <c>IReadOnlyEventStore.QueryEventsAsync(EventQuery)</c>: every
/// filter field, the sequence-ascending ordering contract, and the paging contract. See
/// jasperfx#737.
/// </summary>
/// <remarks>
/// <para>
/// Every filter test here asserts the filter <em>actually filters</em> — that the result is a
/// strict subset of the unfiltered result with the right membership — never merely that the call
/// succeeds. The failure mode this suite exists to prevent is a silently-ignored filter: an
/// implementation that drops a filter it does not understand returns unfiltered results that read
/// as filtered, and a call-succeeds test is green against exactly that bug. The abstraction-side
/// guard rail is <see cref="EventQuery.AssertFiltersAreSupported"/>; this suite is the
/// behavior-side proof for stores that do declare a filter.
/// </para>
/// <para>
/// The <see cref="EventQuery.TenantId"/> filter is deliberately not exercised here — it belongs
/// with <see cref="ConjoinedEventTenancyCompliance{TFixture,TOperations,TQuerySession}"/>, which
/// owns the tenant-scoped store configuration, and it has a test there.
/// </para>
/// </remarks>
public abstract class EventQueryCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_event_query";

        config.AddEventType<CargoLoaded>();
        config.AddEventType<CargoInspected>();
        config.AddEventType<CargoUnloaded>();

        // The metadata filters (CorrelationId, CausationId, UserName) are only honored when the
        // store captures the columns, so the suite turns the capture on.
        config.EnableCorrelationTracking = true;
        config.EnableUserNameTracking = true;

        // No aggregate association: the tag is used purely as a query dimension here.
        config.RegisterTagType<ManifestId>("manifest");
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private Task<PagedEvents> queryAsync(EventQuery query)
        => theFixture.EventStore.OpenReadOnlyEventStore().QueryEventsAsync(query, Cancellation);

    /// <summary>
    /// Everything the store currently holds, in the contract ordering, sized so nothing pages out.
    /// </summary>
    private Task<PagedEvents> queryAllAsync() => queryAsync(new EventQuery { PageSize = 1000 });

    /// <summary>
    /// One save per call, so consecutive calls occupy strictly increasing sequence ranges.
    /// </summary>
    private async Task appendAsync(Guid streamId, params object[] events)
    {
        await using var session = OpenSession();
        EventsFor(session).Append(streamId, events);
        await SaveChangesAsync(session);
    }

    /// <summary>
    /// The standard five-event seed: streamOne holds Loaded/Inspected/Unloaded, streamTwo holds
    /// Loaded/Inspected, interleaved save-by-save so the global sequence alternates between the
    /// streams.
    /// </summary>
    private async Task<(Guid StreamOne, Guid StreamTwo)> seedInterleavedAsync()
    {
        var streamOne = Guid.NewGuid();
        var streamTwo = Guid.NewGuid();

        await appendAsync(streamOne, new CargoLoaded("grain"));
        await appendAsync(streamTwo, new CargoLoaded("coal"));
        await appendAsync(streamOne, new CargoInspected("alice"));
        await appendAsync(streamTwo, new CargoInspected("bob"));
        await appendAsync(streamOne, new CargoUnloaded("Lisbon"));

        return (streamOne, streamTwo);
    }

    [Fact]
    public async Task results_are_ordered_by_sequence_ascending_across_streams()
    {
        var (streamOne, streamTwo) = await seedInterleavedAsync();

        var result = await queryAllAsync();

        result.Events.Count.ShouldBe(5);

        // Strictly ascending, so the ordering is total and by the store-global sequence.
        result.Events.Select(x => x.Sequence)
            .ShouldBe(result.Events.Select(x => x.Sequence).OrderBy(x => x).ToList());
        result.Events.Select(x => x.Sequence).Distinct().Count().ShouldBe(5);

        // The seed interleaved the two streams save-by-save, so a sequence-ascending result
        // alternates between them. A result grouped by stream (or ordered per-stream) fails here.
        result.Events.Select(x => x.StreamId)
            .ShouldBe([streamOne, streamTwo, streamOne, streamTwo, streamOne]);
    }

    [Fact]
    public async Task paging_walks_the_sequence_ordering_and_reports_the_total()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        all.Events.Count.ShouldBe(5);

        var pages = new List<PagedEvents>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            pages.Add(await queryAsync(new EventQuery { PageNumber = pageNumber, PageSize = 2 }));
        }

        foreach (var page in pages)
        {
            // TotalCount is the match count across every page, identical on each of them.
            page.TotalCount.ShouldBe(5);
            page.PageSize.ShouldBe(2);
        }

        pages[0].PageNumber.ShouldBe(1);
        pages[1].PageNumber.ShouldBe(2);
        pages[2].PageNumber.ShouldBe(3);

        pages[0].Events.Count.ShouldBe(2);
        pages[1].Events.Count.ShouldBe(2);
        pages[2].Events.Count.ShouldBe(1);

        // The pages, concatenated, are exactly the unpaged result: disjoint, complete, and in the
        // same sequence-ascending order.
        pages.SelectMany(x => x.Events).Select(x => x.Sequence)
            .ShouldBe(all.Events.Select(x => x.Sequence));
    }

    [Fact]
    public async Task filters_by_a_single_event_type_name()
    {
        await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoInspected>(), PageSize = 1000
        });

        // Fewer than the five seeded events, so the filter demonstrably filtered.
        result.TotalCount.ShouldBe(2);
        result.Events.Count.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.Data is CargoInspected);
    }

    [Fact]
    public async Task filters_by_multiple_event_type_names()
    {
        await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeNames = [EventTypeNameFor<CargoLoaded>(), EventTypeNameFor<CargoUnloaded>()],
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(3);
        result.Events.Count.ShouldBe(3);
        result.Events.ShouldAllBe(x => x.Data is CargoLoaded || x.Data is CargoUnloaded);
        result.Events.ShouldContain(x => x.Data is CargoUnloaded);
    }

    /// <summary>
    /// The documented semantics when both spellings are supplied: the single name unions into the
    /// list. See <see cref="EventQuery.CombinedEventTypeNames"/>.
    /// </summary>
    [Fact]
    public async Task the_single_event_type_name_unions_into_the_list()
    {
        await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            EventTypeNames = [EventTypeNameFor<CargoUnloaded>()],
            PageSize = 1000
        });

        // Union, not intersection (no event is two types at once) and not either-one-wins: both
        // Loaded and Unloaded events return, Inspected does not.
        result.TotalCount.ShouldBe(3);
        result.Events.ShouldContain(x => x.Data is CargoLoaded);
        result.Events.ShouldContain(x => x.Data is CargoUnloaded);
        result.Events.ShouldAllBe(x => !(x.Data is CargoInspected));
    }

    [Fact]
    public async Task filters_by_stream_id()
    {
        var (streamOne, _) = await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery { StreamId = streamOne.ToString(), PageSize = 1000 });

        result.TotalCount.ShouldBe(3);
        result.Events.Count.ShouldBe(3);
        result.Events.ShouldAllBe(x => x.StreamId == streamOne);
    }

    [Fact]
    public async Task filters_by_correlation_id()
    {
        var matching = $"corr-{Guid.NewGuid():N}";
        var other = $"corr-{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            SetCorrelationId(session, matching);
            EventsFor(session).Append(Guid.NewGuid(), new CargoLoaded("grain"), new CargoInspected("alice"));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            SetCorrelationId(session, other);
            EventsFor(session).Append(Guid.NewGuid(), new CargoLoaded("coal"));
            await SaveChangesAsync(session);
        }

        var result = await queryAsync(new EventQuery { CorrelationId = matching, PageSize = 1000 });

        result.TotalCount.ShouldBe(2);
        result.Events.Count.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.CorrelationId == matching);
    }

    [Fact]
    public async Task filters_by_causation_id()
    {
        // Causation is seeded from the ambient activity at session construction, the same way
        // ActivityCorrelationCompliance drives it -- there is deliberately no SetCausationId seam.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        var parent = new Activity("event-query-parent").Start();
        var child = new Activity("event-query-child").Start();

        string? matching;
        try
        {
            await using var session = OpenSession();
            matching = CausationIdFor(session);
            EventsFor(session).Append(Guid.NewGuid(), new CargoLoaded("grain"), new CargoInspected("alice"));
            await SaveChangesAsync(session);
        }
        finally
        {
            child.Stop();
            parent.Stop();
        }

        matching.ShouldNotBeNull();

        // Appended outside the activity scope, so it carries a different (or no) causation id.
        await appendAsync(Guid.NewGuid(), new CargoLoaded("coal"));

        var result = await queryAsync(new EventQuery { CausationId = matching, PageSize = 1000 });

        result.TotalCount.ShouldBe(2);
        result.Events.Count.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.CausationId == matching);
    }

    [Fact]
    public async Task filters_by_user_name()
    {
        await using (var session = OpenSession())
        {
            SetUserName(session, "helen");
            EventsFor(session).Append(Guid.NewGuid(), new CargoLoaded("grain"), new CargoInspected("alice"));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            SetUserName(session, "greta");
            EventsFor(session).Append(Guid.NewGuid(), new CargoLoaded("coal"));
            await SaveChangesAsync(session);
        }

        var result = await queryAsync(new EventQuery { UserName = "helen", PageSize = 1000 });

        result.TotalCount.ShouldBe(2);
        result.Events.Count.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.UserName == "helen");
    }

    [Fact]
    public async Task timestamp_window_is_inclusive_and_filters()
    {
        var streamId = Guid.NewGuid();

        // Three saves separated by real wall-clock gaps, so the middle save's server-assigned
        // timestamps are strictly between its neighbors'.
        await appendAsync(streamId, new CargoLoaded("grain"));
        await Task.Delay(30, Cancellation);
        await appendAsync(streamId, new CargoInspected("alice"), new CargoInspected("carol"));
        await Task.Delay(30, Cancellation);
        await appendAsync(streamId, new CargoUnloaded("Lisbon"));

        // The window bounds come from the store's own read-back, so whatever precision the store
        // persists at is the precision the bounds carry -- and because both bounds equal actual
        // event timestamps, an exclusive comparison on either end loses an event and fails.
        var all = await queryAllAsync();
        all.Events.Count.ShouldBe(4);
        var middle = all.Events.Skip(1).Take(2).ToList();

        var result = await queryAsync(new EventQuery
        {
            TimestampFrom = middle[0].Timestamp,
            TimestampTo = middle[1].Timestamp,
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(2);
        result.Events.Select(x => x.Sequence).ShouldBe(middle.Select(x => x.Sequence));
        result.Events.ShouldAllBe(x => x.Data is CargoInspected);
    }

    [Fact]
    public async Task sequence_window_is_inclusive_and_filters()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        all.Events.Count.ShouldBe(5);
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        var result = await queryAsync(new EventQuery
        {
            SequenceFloor = sequences[1],
            SequenceCeiling = sequences[3],
            PageSize = 1000
        });

        // Both bounds are actual event sequences, so an exclusive comparison on either end drops
        // an endpoint event and fails the exact-membership assertion.
        result.TotalCount.ShouldBe(3);
        result.Events.Select(x => x.Sequence).ShouldBe(sequences.Skip(1).Take(3).ToList());
    }

    [Fact]
    public async Task filters_by_tag_conditions()
    {
        var matching = new ManifestId(Guid.NewGuid());
        var other = new ManifestId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("grain"), matching);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoInspected("alice"), matching);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("coal"), other);

        // And one event carrying no tag at all, so "returned everything" cannot pass.
        await appendAsync(Guid.NewGuid(), new CargoUnloaded("Lisbon"));

        var spec = EventTagQuerySpec.From(new EventTagQuery().Or(matching));

        var result = await queryAsync(new EventQuery { TagConditions = spec, PageSize = 1000 });

        result.TotalCount.ShouldBe(2);
        result.Events.Count.ShouldBe(2);
        result.Events.ShouldContain(x => x.Data is CargoLoaded);
        result.Events.ShouldContain(x => x.Data is CargoInspected);
    }

    /// <summary>
    /// The tag conditions select events, and that selection is AND-combined with every other
    /// filter — the documented composition on <see cref="EventQuery.TagConditions"/>.
    /// </summary>
    [Fact]
    public async Task tag_conditions_combine_with_the_other_filters_as_and()
    {
        var manifest = new ManifestId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("grain"), manifest);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoInspected("alice"), manifest);

        // Same event type as the expected match, but untagged -- caught if the type filter runs
        // and the tag filter is dropped.
        await appendAsync(Guid.NewGuid(), new CargoLoaded("coal"));

        var result = await queryAsync(new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(manifest)),
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Data.ShouldBeOfType<CargoLoaded>().Cargo.ShouldBe("grain");
    }
}
