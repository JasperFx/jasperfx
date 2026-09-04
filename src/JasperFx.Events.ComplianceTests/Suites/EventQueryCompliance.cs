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

    // ---- filter permutations ----
    //
    // Real queries combine filters, and every combination is a fresh chance for one store to
    // AND-compose where another intersects wrongly or drops a filter under the presence of a
    // second. Each test still asserts exact expected membership and TotalCount — the assertions
    // that fail against a silently-dropped filter — never that the call succeeded.

    /// <summary>
    /// Three saves separated by real wall-clock gaps, read back through the store so the returned
    /// timestamps carry the store's own precision. Order: [0] Loaded (batch 1), [1] Inspected and
    /// [2] Loaded (batch 2, one save), [3] Unloaded (batch 3). The type overlap across batches is
    /// the point — a timestamp window and a type filter each exclude something the other keeps.
    /// </summary>
    private async Task<IReadOnlyList<IEvent>> seedTimedBatchesAsync()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(streamId, new CargoLoaded("one"));
        await Task.Delay(40, Cancellation);
        await appendAsync(streamId, new CargoInspected("two-a"), new CargoLoaded("two-b"));
        await Task.Delay(40, Cancellation);
        await appendAsync(streamId, new CargoUnloaded("three"));

        var all = await queryAllAsync();
        all.Events.Count.ShouldBe(4);
        return all.Events;
    }

    [Fact]
    public async Task filters_by_event_type_and_stream_together()
    {
        var (streamOne, _) = await seedInterleavedAsync();

        // Decoys in both directions: streamOne holds a non-Loaded event, streamTwo holds a Loaded.
        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            StreamId = streamOne.ToString(),
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Data.ShouldBeOfType<CargoLoaded>().Cargo.ShouldBe("grain");
    }

    [Fact]
    public async Task filters_by_event_type_and_timestamp_window_together()
    {
        var timed = await seedTimedBatchesAsync();

        // The window admits batch 2 only; the type filter then drops its Inspected event. The
        // Loaded event of batch 1 is the trap for a store that applies the type filter and drops
        // the window.
        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            TimestampFrom = timed[1].Timestamp,
            TimestampTo = timed[2].Timestamp,
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Sequence.ShouldBe(timed[2].Sequence);
    }

    [Fact]
    public async Task filters_by_event_type_and_sequence_window_together()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        // Window admits positions 1..3 (Loaded, Inspected, Inspected); the type filter keeps the
        // two Inspected events. The Inspected at position 2 and 3 both survive, nothing else.
        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoInspected>(),
            SequenceFloor = sequences[1],
            SequenceCeiling = sequences[3],
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(2);
        result.Events.Select(x => x.Sequence).ShouldBe([sequences[2], sequences[3]]);
    }

    [Fact]
    public async Task filters_by_stream_and_sequence_window_together()
    {
        var (streamOne, _) = await seedInterleavedAsync();

        var all = await queryAllAsync();
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        // The window spans positions 1..4; streamOne owns positions 2 and 4 of that span. Its
        // position-0 event is the trap for a dropped window; streamTwo's in-window events are the
        // trap for a dropped stream filter.
        var result = await queryAsync(new EventQuery
        {
            StreamId = streamOne.ToString(),
            SequenceFloor = sequences[1],
            SequenceCeiling = sequences[4],
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(2);
        result.Events.Select(x => x.Sequence).ShouldBe([sequences[2], sequences[4]]);
    }

    [Fact]
    public async Task filters_by_correlation_id_and_event_type_together()
    {
        var matching = $"corr-{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            SetCorrelationId(session, matching);
            EventsFor(session).Append(Guid.NewGuid(), new CargoLoaded("grain"), new CargoInspected("alice"));
            await SaveChangesAsync(session);
        }

        // Same type as the expected match, wrong correlation — the trap for a dropped correlation
        // filter; the matching session's Inspected event is the trap for a dropped type filter.
        await appendAsync(Guid.NewGuid(), new CargoLoaded("coal"));

        var result = await queryAsync(new EventQuery
        {
            CorrelationId = matching,
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Data.ShouldBeOfType<CargoLoaded>().Cargo.ShouldBe("grain");
    }

    [Fact]
    public async Task filters_by_correlation_id_and_timestamp_window_together()
    {
        var matching = $"corr-{Guid.NewGuid():N}";
        var other = $"corr-{Guid.NewGuid():N}";

        async Task appendWithCorrelationAsync(string correlationId, object @event)
        {
            await using var session = OpenSession();
            SetCorrelationId(session, correlationId);
            EventsFor(session).Append(Guid.NewGuid(), @event);
            await SaveChangesAsync(session);
        }

        await appendWithCorrelationAsync(matching, new CargoLoaded("early"));
        await Task.Delay(40, Cancellation);
        await appendWithCorrelationAsync(matching, new CargoInspected("late"));
        await appendWithCorrelationAsync(other, new CargoLoaded("late-other"));

        var all = await queryAllAsync();
        all.Events.Count.ShouldBe(3);

        // The window admits the two late events; the correlation filter keeps one of them. The
        // early matching event is the trap for a dropped window, the late other-correlation event
        // for a dropped correlation filter.
        var result = await queryAsync(new EventQuery
        {
            CorrelationId = matching,
            TimestampFrom = all.Events[1].Timestamp,
            TimestampTo = all.Events[2].Timestamp,
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Sequence.ShouldBe(all.Events[1].Sequence);
    }

    [Fact]
    public async Task filters_by_tags_and_sequence_window_together()
    {
        var manifest = new ManifestId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("tagged-early"), manifest);
        await appendAsync(Guid.NewGuid(), new CargoInspected("untagged"));
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoUnloaded("tagged-late"), manifest);
        await appendAsync(Guid.NewGuid(), new CargoLoaded("untagged-late"));

        var all = await queryAllAsync();
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        // The window admits positions 1..3; the tag matches positions 0 and 2. Intersection: 2.
        var result = await queryAsync(new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(manifest)),
            SequenceFloor = sequences[1],
            SequenceCeiling = sequences[3],
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Sequence.ShouldBe(sequences[2]);
        result.Events.Single().Data.ShouldBeOfType<CargoUnloaded>();
    }

    [Fact]
    public async Task filters_by_user_name_and_event_type_together()
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

        var result = await queryAsync(new EventQuery
        {
            UserName = "helen",
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Data.ShouldBeOfType<CargoLoaded>().Cargo.ShouldBe("grain");
    }

    [Fact]
    public async Task filters_by_multiple_event_types_and_stream_together()
    {
        var (streamOne, _) = await seedInterleavedAsync();

        // streamTwo's Loaded is the trap for a dropped stream filter, streamOne's Inspected for a
        // dropped type list.
        var result = await queryAsync(new EventQuery
        {
            EventTypeNames = [EventTypeNameFor<CargoLoaded>(), EventTypeNameFor<CargoUnloaded>()],
            StreamId = streamOne.ToString(),
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(2);
        result.Events.ShouldContain(x => x.Data is CargoLoaded);
        result.Events.ShouldContain(x => x.Data is CargoUnloaded);
    }

    /// <summary>
    /// Three filters whose pairwise intersections all differ from the three-way one, so a store
    /// that applies any two and drops the third fails on membership.
    /// </summary>
    [Fact]
    public async Task type_and_both_windows_intersect_as_and()
    {
        var timed = await seedTimedBatchesAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            // Batches 2 and 3...
            TimestampFrom = timed[1].Timestamp,
            TimestampTo = timed[3].Timestamp,
            // ...intersected with batches 1 and 2...
            SequenceFloor = timed[0].Sequence,
            SequenceCeiling = timed[2].Sequence,
            // ...is batch 2, whose Loaded event is [2].
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Sequence.ShouldBe(timed[2].Sequence);
    }

    /// <summary>
    /// Every filter the suite's store configuration can express, at once — all of
    /// <see cref="EventQueryFilters.All"/> except <see cref="EventQueryFilters.TenantId"/>, which
    /// belongs to the conjoined tenancy suite. Exactly one seeded event satisfies everything, and
    /// each decoy fails a different single filter, so any dropped filter changes the answer.
    /// </summary>
    [Fact]
    public async Task a_kitchen_sink_query_isolates_exactly_one_event()
    {
        var manifest = new ManifestId(Guid.NewGuid());
        var streamId = Guid.NewGuid();
        var correlation = $"sink-{Guid.NewGuid():N}";

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        var parent = new Activity("sink-parent").Start();
        var child = new Activity("sink-child").Start();

        string? causation;
        try
        {
            await using var session = OpenSession();
            causation = CausationIdFor(session);
            SetCorrelationId(session, correlation);
            SetUserName(session, "sink-user");

            var target = EventsFor(session).BuildEvent(new CargoLoaded("bullseye"));
            target.WithTag(manifest);
            EventsFor(session).Append(streamId, target);

            // Fails only the event type filter.
            var wrongType = EventsFor(session).BuildEvent(new CargoInspected("decoy"));
            wrongType.WithTag(manifest);
            EventsFor(session).Append(streamId, wrongType);

            // Fails only the stream filter.
            var wrongStream = EventsFor(session).BuildEvent(new CargoLoaded("wrong-stream"));
            wrongStream.WithTag(manifest);
            EventsFor(session).Append(Guid.NewGuid(), wrongStream);

            await SaveChangesAsync(session);
        }
        finally
        {
            child.Stop();
            parent.Stop();
        }

        causation.ShouldNotBeNull();

        // Fails the tag, correlation, causation and user filters at once.
        await appendAsync(streamId, new CargoLoaded("untagged"));

        var all = await queryAllAsync();
        all.Events.Count.ShouldBe(4);

        var result = await queryAsync(new EventQuery
        {
            StreamId = streamId.ToString(),
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            EventTypeNames = [EventTypeNameFor<CargoUnloaded>()],
            CorrelationId = correlation,
            CausationId = causation,
            UserName = "sink-user",
            // The windows span everything seeded: present, honored, and deliberately not the
            // thing that decides — the dedicated window tests own that.
            TimestampFrom = all.Events[0].Timestamp,
            TimestampTo = all.Events[^1].Timestamp,
            SequenceFloor = all.Events[0].Sequence,
            SequenceCeiling = all.Events[^1].Sequence,
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(manifest)),
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Data.ShouldBeOfType<CargoLoaded>().Cargo.ShouldBe("bullseye");
    }

    // ---- window edge permutations ----

    [Fact]
    public async Task timestamp_from_alone_is_a_half_open_window()
    {
        var timed = await seedTimedBatchesAsync();

        var result = await queryAsync(new EventQuery { TimestampFrom = timed[1].Timestamp, PageSize = 1000 });

        result.TotalCount.ShouldBe(3);
        result.Events.Select(x => x.Sequence).ShouldBe(timed.Skip(1).Select(x => x.Sequence).ToList());
    }

    [Fact]
    public async Task timestamp_to_alone_is_a_half_open_window()
    {
        var timed = await seedTimedBatchesAsync();

        var result = await queryAsync(new EventQuery { TimestampTo = timed[2].Timestamp, PageSize = 1000 });

        result.TotalCount.ShouldBe(3);
        result.Events.Select(x => x.Sequence).ShouldBe(timed.Take(3).Select(x => x.Sequence).ToList());
    }

    [Fact]
    public async Task sequence_floor_alone_is_a_half_open_window()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        var result = await queryAsync(new EventQuery { SequenceFloor = sequences[2], PageSize = 1000 });

        result.TotalCount.ShouldBe(3);
        result.Events.Select(x => x.Sequence).ShouldBe(sequences.Skip(2).ToList());
    }

    [Fact]
    public async Task sequence_ceiling_alone_is_a_half_open_window()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        var result = await queryAsync(new EventQuery { SequenceCeiling = sequences[2], PageSize = 1000 });

        result.TotalCount.ShouldBe(3);
        result.Events.Select(x => x.Sequence).ShouldBe(sequences.Take(3).ToList());
    }

    [Fact]
    public async Task a_sequence_window_of_one_sequence_returns_exactly_that_event()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        var target = all.Events[2];

        var result = await queryAsync(new EventQuery
        {
            SequenceFloor = target.Sequence,
            SequenceCeiling = target.Sequence,
            PageSize = 1000
        });

        // Fails on either exclusive end: an exclusive floor or ceiling makes this empty.
        result.TotalCount.ShouldBe(1);
        result.Events.Single().Sequence.ShouldBe(target.Sequence);
    }

    [Fact]
    public async Task a_timestamp_window_of_one_instant_returns_exactly_the_events_at_it()
    {
        var timed = await seedTimedBatchesAsync();

        // Batch 3 is a single event separated from its neighbors by real wall-clock gaps, so its
        // read-back timestamp identifies it alone at the store's own precision.
        var target = timed[3];

        var result = await queryAsync(new EventQuery
        {
            TimestampFrom = target.Timestamp,
            TimestampTo = target.Timestamp,
            PageSize = 1000
        });

        result.TotalCount.ShouldBe(1);
        result.Events.Single().Sequence.ShouldBe(target.Sequence);
    }

    /// <summary>
    /// Inclusive-boundary proof at both extremes of the store: windows pinned to the first and
    /// last events return everything. A store excluding either end loses exactly one event here.
    /// </summary>
    [Fact]
    public async Task windows_pinned_to_the_first_and_last_events_are_inclusive_at_both_extremes()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        var sequences = all.Events.Select(x => x.Sequence).ToList();

        var bySequence = await queryAsync(new EventQuery
        {
            SequenceFloor = sequences[0],
            SequenceCeiling = sequences[^1],
            PageSize = 1000
        });

        bySequence.TotalCount.ShouldBe(5);
        bySequence.Events.Select(x => x.Sequence).ShouldBe(sequences);

        var byTimestamp = await queryAsync(new EventQuery
        {
            TimestampFrom = all.Events[0].Timestamp,
            TimestampTo = all.Events[^1].Timestamp,
            PageSize = 1000
        });

        byTimestamp.TotalCount.ShouldBe(5);
        byTimestamp.Events.Select(x => x.Sequence).ShouldBe(sequences);
    }

    [Fact]
    public async Task a_window_matching_nothing_is_an_empty_answer_not_an_error()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();
        var maxSequence = all.Events[^1].Sequence;

        var bySequence = await queryAsync(new EventQuery
        {
            SequenceFloor = maxSequence + 1000,
            SequenceCeiling = maxSequence + 2000,
            PageSize = 1000
        });

        bySequence.TotalCount.ShouldBe(0);
        bySequence.Events.ShouldBeEmpty();

        var byTimestamp = await queryAsync(new EventQuery
        {
            TimestampFrom = DateTimeOffset.UtcNow.AddYears(10),
            PageSize = 1000
        });

        byTimestamp.TotalCount.ShouldBe(0);
        byTimestamp.Events.ShouldBeEmpty();
    }

    /// <summary>
    /// The documented contract on <see cref="EventQuery"/>: an inverted window is a well-formed
    /// range that contains nothing — an empty page, never an error.
    /// </summary>
    [Fact]
    public async Task an_inverted_window_matches_nothing_by_contract()
    {
        await seedInterleavedAsync();

        var all = await queryAllAsync();

        var bySequence = await queryAsync(new EventQuery
        {
            SequenceFloor = all.Events[^1].Sequence,
            SequenceCeiling = all.Events[0].Sequence,
            PageSize = 1000
        });

        bySequence.TotalCount.ShouldBe(0);
        bySequence.Events.ShouldBeEmpty();

        var byTimestamp = await queryAsync(new EventQuery
        {
            TimestampFrom = all.Events[^1].Timestamp.AddDays(1),
            TimestampTo = all.Events[0].Timestamp,
            PageSize = 1000
        });

        byTimestamp.TotalCount.ShouldBe(0);
        byTimestamp.Events.ShouldBeEmpty();
    }

    // ---- paging × filtering ----

    /// <summary>
    /// Paging applies to the FILTERED, sequence-ordered result: pages are disjoint, complete and
    /// ordered across page boundaries, TotalCount is the filtered total on every page, the last
    /// page is short, and a page past the end is empty with the total intact.
    /// </summary>
    [Fact]
    public async Task paging_composes_with_filtering()
    {
        // Seven Inspected events interleaved with three Loaded, across streams, one save each —
        // so the Inspected sequences are non-contiguous and a store paging BEFORE filtering
        // produces short or bleeding pages.
        //
        // ⚠️ The noise predicate must yield exactly 3 noise / 7 matches over 0..9: `i % 3 == 0`
        // yields FOUR noise events (0, 3, 6, 9) and made this fact unpassable on every store —
        // caught independently by the Marten and Fisher enrollments, both answering the correct 6
        // against the asserted 7.
        for (var i = 0; i < 10; i++)
        {
            object @event = i % 3 == 2 ? new CargoLoaded($"noise-{i}") : new CargoInspected($"match-{i}");
            await appendAsync(Guid.NewGuid(), @event);
        }

        var unpaged = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoInspected>(), PageSize = 1000
        });
        unpaged.TotalCount.ShouldBe(7);

        var pages = new List<PagedEvents>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            pages.Add(await queryAsync(new EventQuery
            {
                EventTypeName = EventTypeNameFor<CargoInspected>(),
                PageNumber = pageNumber,
                PageSize = 3
            }));
        }

        foreach (var page in pages)
        {
            page.TotalCount.ShouldBe(7);
            page.Events.ShouldAllBe(x => x.Data is CargoInspected);
        }

        pages[0].Events.Count.ShouldBe(3);
        pages[1].Events.Count.ShouldBe(3);
        pages[2].Events.Count.ShouldBe(1);

        pages.SelectMany(x => x.Events).Select(x => x.Sequence)
            .ShouldBe(unpaged.Events.Select(x => x.Sequence));

        var pastTheEnd = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoInspected>(),
            PageNumber = 10,
            PageSize = 3
        });

        // An empty page, but a truthful total — a consumer walking pages needs the count to know
        // it overshot rather than that nothing matched.
        pastTheEnd.Events.ShouldBeEmpty();
        pastTheEnd.TotalCount.ShouldBe(7);
    }

    // ---- multi-type permutations ----

    [Fact]
    public async Task an_unknown_event_type_name_unions_quietly_with_known_ones()
    {
        await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeNames = [EventTypeNameFor<CargoLoaded>(), "no_such_event_type"],
            PageSize = 1000
        });

        // The unknown name contributes nothing; it is not an error and it must not damage the
        // half of the union that matches.
        result.TotalCount.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.Data is CargoLoaded);
    }

    [Fact]
    public async Task duplicate_event_type_names_do_not_double_count()
    {
        await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeNames =
                [EventTypeNameFor<CargoInspected>(), EventTypeNameFor<CargoInspected>()],
            PageSize = 1000
        });

        // Two spellings of one condition: the same two events, once each — a UNION ALL shape
        // (or a doubled join) reads back four and fails both assertions.
        result.TotalCount.ShouldBe(2);
        result.Events.Select(x => x.Sequence).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task a_single_name_overlapping_the_list_dedupes()
    {
        await seedInterleavedAsync();

        var result = await queryAsync(new EventQuery
        {
            EventTypeName = EventTypeNameFor<CargoLoaded>(),
            EventTypeNames = [EventTypeNameFor<CargoLoaded>(), EventTypeNameFor<CargoUnloaded>()],
            PageSize = 1000
        });

        // The union of {Loaded} and {Loaded, Unloaded} is three events, with the two Loaded
        // events appearing once each.
        result.TotalCount.ShouldBe(3);
        result.Events.Count(x => x.Data is CargoLoaded).ShouldBe(2);
        result.Events.Count(x => x.Data is CargoUnloaded).ShouldBe(1);
    }

    // ---- tag permutations ----

    [Fact]
    public async Task overlapping_tag_conditions_do_not_duplicate_events()
    {
        var first = new ManifestId(Guid.NewGuid());
        var second = new ManifestId(Guid.NewGuid());

        await using var session = OpenSession();

        // Carries BOTH tags, so both OR conditions match it.
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("both"), first, second);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoInspected("first-only"), first);
        await appendAsync(Guid.NewGuid(), new CargoLoaded("untagged"));

        var result = await queryAsync(new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(first).Or(second)),
            PageSize = 1000
        });

        // The doubly-tagged event appears once. TotalCount counts distinct events, not condition
        // hits — a tag-table join without a distinct reads three rows here.
        result.TotalCount.ShouldBe(2);
        result.Events.Select(x => x.Sequence).Distinct().Count().ShouldBe(2);
        result.Events.ShouldContain(x => x.Data is CargoLoaded);
        result.Events.ShouldContain(x => x.Data is CargoInspected);
    }

    [Fact]
    public async Task a_tag_condition_scoped_to_an_event_type_narrows_the_unscoped_match()
    {
        var manifest = new ManifestId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("grain"), manifest);
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoInspected("alice"), manifest);

        var unscoped = await queryAsync(new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(manifest)),
            PageSize = 1000
        });

        unscoped.TotalCount.ShouldBe(2);

        // The same tag value, but the condition itself carries the event type — the
        // EventTagQueryConditionSpec.EventType slot, distinct from EventQuery's own type filter.
        var scoped = await queryAsync(new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(
                new EventTagQuery().Or<CargoLoaded, ManifestId>(manifest)),
            PageSize = 1000
        });

        scoped.TotalCount.ShouldBe(1);
        scoped.Events.Single().Data.ShouldBeOfType<CargoLoaded>();
    }

    [Fact]
    public async Task tagged_events_across_streams_return_in_global_sequence_order()
    {
        var manifest = new ManifestId(Guid.NewGuid());

        await using var session = OpenSession();
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoLoaded("first"), manifest);
        await appendAsync(Guid.NewGuid(), new CargoInspected("noise-1"));
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoInspected("second"), manifest);
        await appendAsync(Guid.NewGuid(), new CargoLoaded("noise-2"));
        await AppendTaggedEventAsync(session, Guid.NewGuid(), new CargoUnloaded("third"), manifest);

        var all = await queryAllAsync();
        var taggedSequences = new[] { all.Events[0], all.Events[2], all.Events[4] }
            .Select(x => x.Sequence).ToList();

        var result = await queryAsync(new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(manifest)),
            PageSize = 1000
        });

        // Three streams, so a per-stream or per-tag-table ordering shows here where the
        // single-stream tests cannot see it.
        result.TotalCount.ShouldBe(3);
        result.Events.Select(x => x.Sequence).ShouldBe(taggedSequences);
    }

    // ---- filters against data that contains none of it ----

    /// <summary>
    /// Every filter, one at a time, against seeded data that matches none of it: a truthful zero
    /// each time, never a throw. The mirror image of the guard rail — "I support this filter and
    /// nothing matches" must stay distinguishable from "I ignored this filter".
    /// </summary>
    [Fact]
    public async Task every_filter_alone_returns_an_empty_answer_when_nothing_matches()
    {
        await seedInterleavedAsync();

        var nonMatching = new EventQuery[]
        {
            new() { EventTypeName = "no_such_event_type" },
            new() { EventTypeNames = ["no_such_event_type", "nor_this_one"] },
            new() { StreamId = Guid.NewGuid().ToString() },
            new() { CorrelationId = $"corr-{Guid.NewGuid():N}" },
            new() { CausationId = $"cause-{Guid.NewGuid():N}" },
            new() { UserName = $"user-{Guid.NewGuid():N}" },
            new() { TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(new ManifestId(Guid.NewGuid()))) }
        };

        foreach (var query in nonMatching)
        {
            query.PageSize = 1000;
            var result = await queryAsync(query);

            result.TotalCount.ShouldBe(0,
                $"the {query.SpecifiedFilters} filter should have matched nothing");
            result.Events.ShouldBeEmpty();
        }
    }
}
