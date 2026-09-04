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
    }

    [Fact]
    public void a_fully_loaded_query_specifies_all()
    {
        var query = new EventQuery
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
            SequenceCeiling = 100,
            TagConditions = EventTagQuerySpec.From(new EventTagQuery().Or(new QueryManifest("m-1")))
        };

        query.SpecifiedFilters.ShouldBe(EventQueryFilters.All);

        // A fully implemented store declares All and takes anything.
        Should.NotThrow(() => query.AssertFiltersAreSupported(EventQueryFilters.All));
    }

    [Fact]
    public void baseline_is_exactly_the_pre_737_surface()
    {
        EventQueryFilters.Baseline.ShouldBe(
            EventQueryFilters.EventTypeName | EventQueryFilters.StreamId | EventQueryFilters.CorrelationId |
            EventQueryFilters.CausationId | EventQueryFilters.UserName | EventQueryFilters.TenantId);

        EventQueryFilters.TimestampWindow.ShouldBe(EventQueryFilters.TimestampFrom | EventQueryFilters.TimestampTo);
        EventQueryFilters.SequenceWindow.ShouldBe(EventQueryFilters.SequenceFloor | EventQueryFilters.SequenceCeiling);
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
}
