using System.Text.Json;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests;

// Coverage for jasperfx#737: the broadened EventQuery — time/sequence windows, multiple event
// type names, folded tag conditions — and its guard rail, AssertFiltersAreSupported, which is
// what keeps a store from silently ignoring a filter it has not implemented. The behavioral
// (does-it-actually-filter) half lives in the shared compliance suite, EventQueryCompliance.
public class EventQueryTests
{
    private record QueryManifest(string Value);

    private record ManifestOpened(string Name);

    [Fact]
    public void specified_filters_is_none_on_an_empty_query()
    {
        new EventQuery().SpecifiedFilters.ShouldBe(EventQueryFilters.None);
    }

    [Fact]
    public void paging_is_not_a_filter()
    {
        var query = new EventQuery { PageNumber = 7, PageSize = 3 };

        query.SpecifiedFilters.ShouldBe(EventQueryFilters.None);

        // And therefore never trips the guard rail, even against a store declaring nothing.
        Should.NotThrow(() => query.AssertFiltersAreSupported(EventQueryFilters.None));
    }

    [Fact]
    public void specified_filters_reflects_each_supplied_field()
    {
        new EventQuery { EventTypeName = "a" }.SpecifiedFilters.ShouldBe(EventQueryFilters.EventTypeName);
        new EventQuery { EventTypeNames = ["a", "b"] }.SpecifiedFilters.ShouldBe(EventQueryFilters.EventTypeNames);
        new EventQuery { StreamId = "s" }.SpecifiedFilters.ShouldBe(EventQueryFilters.StreamId);
        new EventQuery { CorrelationId = "c" }.SpecifiedFilters.ShouldBe(EventQueryFilters.CorrelationId);
        new EventQuery { CausationId = "c" }.SpecifiedFilters.ShouldBe(EventQueryFilters.CausationId);
        new EventQuery { UserName = "u" }.SpecifiedFilters.ShouldBe(EventQueryFilters.UserName);
        new EventQuery { TenantId = "t" }.SpecifiedFilters.ShouldBe(EventQueryFilters.TenantId);
        new EventQuery { TimestampFrom = DateTimeOffset.UtcNow }.SpecifiedFilters.ShouldBe(EventQueryFilters.TimestampFrom);
        new EventQuery { TimestampTo = DateTimeOffset.UtcNow }.SpecifiedFilters.ShouldBe(EventQueryFilters.TimestampTo);
        new EventQuery { SequenceFloor = 1 }.SpecifiedFilters.ShouldBe(EventQueryFilters.SequenceFloor);
        new EventQuery { SequenceCeiling = 100 }.SpecifiedFilters.ShouldBe(EventQueryFilters.SequenceCeiling);

        var spec = EventTagQuerySpec.From(new EventTagQuery().Or(new QueryManifest("m-1")));
        new EventQuery { TagConditions = spec }.SpecifiedFilters.ShouldBe(EventQueryFilters.TagConditions);

        new EventQuery { TagValues = { ["manifest"] = "m-1" } }
            .SpecifiedFilters.ShouldBe(EventQueryFilters.TagValues);
    }

    /// <summary>
    /// Every filter at once, in each of its two tag spellings. There is deliberately no single query
    /// that specifies <see cref="EventQueryFilters.All"/>: the two tag members are exclusive
    /// (jasperfx#801), so <c>All</c> is what a store declares, not what one query can carry.
    /// </summary>
    [Fact]
    public void a_fully_loaded_query_specifies_all_but_one_tag_spelling()
    {
        EventQuery loaded() => new()
        {
            EventTypeName = "a",
            EventTypeNames = ["b"],
            StreamId = "s",
            CorrelationId = "corr",
            CausationId = "cause",
            UserName = "u",
            TenantId = "t",
            TimestampFrom = DateTimeOffset.UtcNow.AddDays(-1),
            TimestampTo = DateTimeOffset.UtcNow,
            SequenceFloor = 1,
            SequenceCeiling = 100
        };

        var rich = loaded();
        rich.TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(new QueryManifest("m-1")));
        rich.SpecifiedFilters.ShouldBe(EventQueryFilters.All & ~EventQueryFilters.TagValues);

        var lossy = loaded();
        lossy.TagValues["manifest"] = "m-1";
        lossy.SpecifiedFilters.ShouldBe(EventQueryFilters.All & ~EventQueryFilters.TagConditions);

        // A fully implemented store declares All and takes either.
        Should.NotThrow(() => rich.AssertFiltersAreSupported(EventQueryFilters.All));
        Should.NotThrow(() => lossy.AssertFiltersAreSupported(EventQueryFilters.All));
    }

    [Fact]
    public void an_empty_tag_values_dictionary_is_no_filter()
    {
        // Same reason as the empty EventTypeNames list: the default instance must not read as a
        // supplied filter, or every existing caller would trip the guard rail on a store that has
        // not implemented jasperfx#801 yet.
        new EventQuery().SpecifiedFilters.ShouldBe(EventQueryFilters.None);
        new EventQuery { TagValues = new Dictionary<string, string>() }
            .SpecifiedFilters.ShouldBe(EventQueryFilters.None);
    }

    [Fact]
    public void the_two_tag_spellings_cannot_be_combined()
    {
        var query = new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(new QueryManifest("m-1"))),
            TagValues = { ["manifest"] = "m-1" }
        };

        var ex = Should.Throw<ArgumentException>(() => query.AssertIsWellFormed());
        ex.Message.ShouldContain("EventQuery.TagConditions");
        ex.Message.ShouldContain("EventQuery.TagValues");

        // And the guard rail every implementation already calls carries the same refusal, so a store
        // does not have to remember a second assertion. It refuses ahead of the support check —
        // "you cannot ask for both" beats "I do not support one of them".
        Should.Throw<ArgumentException>(() => query.AssertFiltersAreSupported(EventQueryFilters.All));
        Should.Throw<ArgumentException>(() => query.AssertFiltersAreSupported(EventQueryFilters.Baseline));
    }

    [Fact]
    public void either_tag_spelling_alone_is_well_formed()
    {
        Should.NotThrow(() => new EventQuery
        {
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(new QueryManifest("m-1")))
        }.AssertIsWellFormed());

        Should.NotThrow(() => new EventQuery { TagValues = { ["manifest"] = "m-1" } }.AssertIsWellFormed());
        Should.NotThrow(() => new EventQuery().AssertIsWellFormed());
    }

    [Fact]
    public void a_store_without_the_lossy_tag_form_refuses_it_by_name()
    {
        var query = new EventQuery { StreamId = "s", TagValues = { ["manifest"] = "m-1" } };

        // The jasperfx#801 member joined EventQueryFilters.All, so a store that has not implemented
        // it must subtract it from its declaration — and then refuse the filter by name rather than
        // returning events that ignore it.
        var ex = Should.Throw<NotSupportedException>(
            () => query.AssertFiltersAreSupported(EventQueryFilters.All & ~EventQueryFilters.TagValues));

        ex.Message.ShouldContain("EventQuery.TagValues");
        ex.Message.ShouldNotContain("EventQuery.StreamId");
    }

    [Fact]
    public void baseline_is_exactly_the_pre_737_surface()
    {
        EventQueryFilters.Baseline.ShouldBe(
            EventQueryFilters.EventTypeName | EventQueryFilters.StreamId | EventQueryFilters.CorrelationId |
            EventQueryFilters.CausationId | EventQueryFilters.UserName | EventQueryFilters.TenantId);

        EventQueryFilters.TimestampWindow.ShouldBe(EventQueryFilters.TimestampFrom | EventQueryFilters.TimestampTo);
        EventQueryFilters.SequenceWindow.ShouldBe(EventQueryFilters.SequenceFloor | EventQueryFilters.SequenceCeiling);
        EventQueryFilters.Tags.ShouldBe(EventQueryFilters.TagConditions | EventQueryFilters.TagValues);
    }

    [Fact]
    public void passes_when_every_supplied_filter_is_declared()
    {
        var query = new EventQuery { EventTypeName = "a", StreamId = "s" };

        Should.NotThrow(() => query.AssertFiltersAreSupported(EventQueryFilters.Baseline));
    }

    [Fact]
    public void throws_not_supported_naming_exactly_the_unsupported_fields()
    {
        var query = new EventQuery
        {
            StreamId = "s",
            TimestampFrom = DateTimeOffset.UtcNow,
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(new QueryManifest("m-1")))
        };

        // A store still on the pre-737 surface: StreamId is fine, the two new filters are not.
        var ex = Should.Throw<NotSupportedException>(
            () => query.AssertFiltersAreSupported(EventQueryFilters.Baseline));

        ex.Message.ShouldContain("EventQuery.TimestampFrom");
        ex.Message.ShouldContain("EventQuery.TagConditions");
        ex.Message.ShouldNotContain("EventQuery.StreamId");
    }

    [Fact]
    public void an_empty_event_type_names_list_is_no_filter()
    {
        // The default instance list must not read as a supplied filter, or every old caller
        // would suddenly trip guard rails on stores that have not implemented the new field.
        new EventQuery { EventTypeNames = [] }.SpecifiedFilters.ShouldBe(EventQueryFilters.None);
        new EventQuery().CombinedEventTypeNames().ShouldBeEmpty();
    }

    [Fact]
    public void combined_event_type_names_folds_the_single_name_into_the_list()
    {
        new EventQuery { EventTypeName = "a" }.CombinedEventTypeNames().ShouldBe(["a"]);
        new EventQuery { EventTypeNames = ["b", "c"] }.CombinedEventTypeNames().ShouldBe(["b", "c"]);

        // Both supplied: the union, single name first, so one code path serves both spellings.
        new EventQuery { EventTypeName = "a", EventTypeNames = ["b", "c"] }
            .CombinedEventTypeNames().ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void combined_event_type_names_is_distinct()
    {
        new EventQuery { EventTypeName = "a", EventTypeNames = ["a", "b", "b"] }
            .CombinedEventTypeNames().ShouldBe(["a", "b"]);
    }

    [Fact]
    public void round_trips_through_json_as_a_wire_shape()
    {
        var original = new EventQuery
        {
            EventTypeNames = ["manifest_opened"],
            StreamId = "stream-1",
            TenantId = "tenant-1",
            TimestampFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            TimestampTo = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
            SequenceFloor = 10,
            SequenceCeiling = 200,
            PageNumber = 2,
            PageSize = 25,
            TagConditions = EventTagQuerySpec.From(
                new EventTagQuery().Or<ManifestOpened, QueryManifest>(new QueryManifest("m-1")))
        };

        var json = JsonSerializer.Serialize(original);
        var query = JsonSerializer.Deserialize<EventQuery>(json);

        query.ShouldNotBeNull();
        query.EventTypeNames.ShouldBe(["manifest_opened"]);
        query.StreamId.ShouldBe("stream-1");
        query.TenantId.ShouldBe("tenant-1");
        query.TimestampFrom.ShouldBe(original.TimestampFrom);
        query.TimestampTo.ShouldBe(original.TimestampTo);
        query.SequenceFloor.ShouldBe(10);
        query.SequenceCeiling.ShouldBe(200);
        query.PageNumber.ShouldBe(2);
        query.PageSize.ShouldBe(25);

        query.SpecifiedFilters.ShouldBe(original.SpecifiedFilters);

        // The folded tag conditions survive the hop and resolve back to CLR types, exactly as
        // EventTagQuerySpec promises on its own (jasperfx#545) — folding it into EventQuery must
        // not cost that.
        query.TagConditions.ShouldNotBeNull();
        var resolver = EventTagQuerySpec.ResolverFor([typeof(QueryManifest), typeof(ManifestOpened)]);
        var rehydrated = query.TagConditions.Resolve(resolver);

        var condition = rehydrated.Conditions.ShouldHaveSingleItem();
        condition.EventType.ShouldBe(typeof(ManifestOpened));
        condition.TagType.ShouldBe(typeof(QueryManifest));
        condition.TagValue.ShouldBe(new QueryManifest("m-1"));
    }

    /// <summary>
    /// The lossy tag form's whole reason to exist is a caller that holds a name/value pair and no CLR
    /// type graph — which is a caller on the far side of a wire — so it has to survive the hop.
    /// </summary>
    [Fact]
    public void the_lossy_tag_form_round_trips_through_json()
    {
        var original = new EventQuery
        {
            EventTypeNames = ["manifest_opened"],
            SequenceFloor = 10,
            TagValues = { ["QueryManifest"] = "m-1", ["manifest"] = "m-2" }
        };

        var query = JsonSerializer.Deserialize<EventQuery>(JsonSerializer.Serialize(original));

        query.ShouldNotBeNull();
        query.TagValues.Count.ShouldBe(2);
        query.TagValues["QueryManifest"].ShouldBe("m-1");
        query.TagValues["manifest"].ShouldBe("m-2");
        query.TagConditions.ShouldBeNull();

        query.SpecifiedFilters.ShouldBe(original.SpecifiedFilters);
        query.SpecifiedFilters.ShouldBe(
            EventQueryFilters.EventTypeNames | EventQueryFilters.SequenceFloor | EventQueryFilters.TagValues);
    }
}
