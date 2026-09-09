using System;
using System.Linq;
using System.Text.Json;
using JasperFx.Events;
using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#737 — the <c>event-query</c> command's flag-to-<see cref="EventQuery"/> mapping,
/// validation, and <c>--tags</c> JSON parsing, pinned without a host or a database. Store
/// selection is shared with <c>projection-run</c> and covered by
/// <see cref="ProjectionRunSourceTests"/>.
/// </summary>
public class EventQueryInputTests
{
    [Fact]
    public void an_empty_input_builds_an_unfiltered_default_page_query()
    {
        var query = new EventQueryInput().BuildQuery();

        query.SpecifiedFilters.ShouldBe(EventQueryFilters.None);
        query.PageNumber.ShouldBe(1);
        query.PageSize.ShouldBe(50);
        new EventQueryInput().Validate().ShouldBeNull();
    }

    [Fact]
    public void every_flag_maps_onto_its_query_field()
    {
        var input = new EventQueryInput
        {
            StreamFlag = "stream-1",
            EventTypeFlag = "cargo_loaded",
            CorrelationIdFlag = "corr-1",
            CausationIdFlag = "cause-1",
            UserNameFlag = "helen",
            TenantFlag = "acme",
            TimestampFromFlag = "2026-09-01T00:00:00Z",
            TimestampToFlag = "2026-09-02T00:00:00Z",
            SequenceFloorFlag = 10,
            SequenceCeilingFlag = 200,
            TagsFlag = "{\"ManifestId\":\"m-1\"}",
            PageFlag = 3,
            PageSizeFlag = 25
        };

        input.Validate().ShouldBeNull();
        var query = input.BuildQuery();

        query.StreamId.ShouldBe("stream-1");
        query.EventTypeNames.ShouldBe(["cargo_loaded"]);
        query.EventTypeName.ShouldBeNull();
        query.CorrelationId.ShouldBe("corr-1");
        query.CausationId.ShouldBe("cause-1");
        query.UserName.ShouldBe("helen");
        query.TenantId.ShouldBe("acme");
        query.TimestampFrom.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        query.TimestampTo.ShouldBe(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        query.SequenceFloor.ShouldBe(10);
        query.SequenceCeiling.ShouldBe(200);
        query.TagValues["ManifestId"].ShouldBe("m-1");
        query.PageNumber.ShouldBe(3);
        query.PageSize.ShouldBe(25);

        // Everything but the single-name spelling (the CLI always builds the list form) and the rich
        // TagConditions spelling — the command's --tags flag carries names, so it builds the lossy
        // TagValues form, and the two tag members are mutually exclusive (jasperfx#801/#803).
        query.SpecifiedFilters.ShouldBe(
            EventQueryFilters.All & ~EventQueryFilters.EventTypeName & ~EventQueryFilters.TagConditions);
        query.TagConditions.ShouldBeNull();
    }

    [Fact]
    public void comma_separated_event_types_become_the_list_form()
    {
        var query = new EventQueryInput { EventTypeFlag = "cargo_loaded, cargo_unloaded ,," }.BuildQuery();

        // Trimmed, empties dropped, and always through EventTypeNames — the CLI never sets the
        // single-name member, so CombinedEventTypeNames() is exactly the flag's list.
        query.EventTypeNames.ShouldBe(["cargo_loaded", "cargo_unloaded"]);
        query.EventTypeName.ShouldBeNull();
        query.CombinedEventTypeNames().ShouldBe(["cargo_loaded", "cargo_unloaded"]);
    }

    /// <summary>
    /// jasperfx#803: the flag carries tag NAMES, not type descriptors. The previous shape built an
    /// <c>EventTagQuerySpec</c> whose TypeDescriptor.FullName was the bare property name, which
    /// <c>EventTagQuerySpec.ResolverFor</c> — a full-name match — could never resolve for a tag type
    /// in a namespace, so the flag threw <c>UnknownTagQueryTypeException</c> against every real store.
    /// </summary>
    [Fact]
    public void tags_parse_into_the_lossy_name_value_filter()
    {
        var (tags, error) = EventQueryInput.TryParseTags("{\"StudentId\":\"s-1\",\"course\":\"c-2\"}");

        error.ShouldBeNull();
        tags.ShouldNotBeNull();
        tags.Count.ShouldBe(2);

        // Either spelling passes through untouched — the store resolves the name against its own tag
        // graph, where the graph actually is.
        tags["StudentId"].ShouldBe("s-1");
        tags["course"].ShouldBe("c-2");
    }

    [Fact]
    public void a_scalar_tag_value_keeps_its_literal_spelling()
    {
        // The store matches on the stored tag value's string form, so a number or boolean carries
        // through as the text the operator wrote rather than being quoted or reformatted.
        var (tags, error) = EventQueryInput.TryParseTags("{\"OrderId\":42,\"Active\":true}");

        error.ShouldBeNull();
        tags.ShouldNotBeNull();
        tags["OrderId"].ShouldBe("42");
        tags["Active"].ShouldBe("true");
    }

    [Fact]
    public void a_composite_tag_value_is_refused()
    {
        // A tag value is always a strong-typed id over a primitive, so an object or array is a
        // mangled invocation. Refused rather than stringified: a stringified object matches nothing
        // and would read back as "no such events".
        var (tags, error) = EventQueryInput.TryParseTags("{\"OrderId\":{\"value\":42}}");

        tags.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("OrderId");
        error.ShouldContain("object");

        EventQueryInput.TryParseTags("{\"OrderId\":[1,2]}").Error.ShouldNotBeNull();
    }

    [Fact]
    public void a_missing_tags_flag_is_no_tag_filter()
    {
        EventQueryInput.TryParseTags(null).ShouldBe((null, null));
        EventQueryInput.TryParseTags("").ShouldBe((null, null));

        new EventQueryInput().BuildQuery().TagValues.ShouldBeEmpty();
    }

    [Fact]
    public void the_tags_flag_lands_on_tag_values_not_tag_conditions()
    {
        var query = new EventQueryInput { TagsFlag = "{\"manifest\":\"m-1\",\"shipper\":\"acme\"}" }.BuildQuery();

        query.TagConditions.ShouldBeNull();
        query.TagValues.Count.ShouldBe(2);
        query.TagValues["manifest"].ShouldBe("m-1");
        query.TagValues["shipper"].ShouldBe("acme");

        // And the query stays well formed, so the store's guard rail passes it through.
        Should.NotThrow(() => query.AssertIsWellFormed());
        query.SpecifiedFilters.ShouldBe(EventQueryFilters.TagValues);
    }

    [Fact]
    public void unparseable_tags_json_is_refused()
    {
        var (tags, error) = EventQueryInput.TryParseTags("{not json");

        tags.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("--tags is not valid JSON");
    }

    [Fact]
    public void tags_that_are_not_an_object_are_refused()
    {
        var (tags, error) = EventQueryInput.TryParseTags("[\"StudentId\"]");

        tags.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("must be a JSON object");
    }

    [Fact]
    public void an_empty_tags_object_is_refused_rather_than_ignored()
    {
        // {} filters nothing; running the query unfiltered would be the silently-ignored-filter
        // failure mode the whole jasperfx#737 surface is built to refuse.
        var (tags, error) = EventQueryInput.TryParseTags("{}");

        tags.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("empty JSON object");
    }

    [Fact]
    public void a_null_tag_value_is_refused()
    {
        var (tags, error) = EventQueryInput.TryParseTags("{\"StudentId\":null}");

        tags.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("StudentId");
    }

    [Fact]
    public void tag_errors_surface_through_validate()
    {
        new EventQueryInput { TagsFlag = "{}" }.Validate().ShouldNotBeNull();
        new EventQueryInput { TagsFlag = "{\"ManifestId\":\"m-1\"}" }.Validate().ShouldBeNull();
    }

    [Fact]
    public void paging_flags_are_validated()
    {
        new EventQueryInput { PageFlag = 0 }.Validate().ShouldBe("--page must be 1 or greater");
        new EventQueryInput { PageSizeFlag = 0 }.Validate().ShouldBe("--page-size must be greater than zero");
    }

    [Fact]
    public void unparseable_timestamps_are_refused_with_the_offending_flag_named()
    {
        new EventQueryInput { TimestampFromFlag = "not-a-time" }.Validate()!
            .ShouldContain("--timestamp-from");
        new EventQueryInput { TimestampToFlag = "also-not" }.Validate()!
            .ShouldContain("--timestamp-to");
    }

    [Fact]
    public void inverted_windows_are_refused()
    {
        new EventQueryInput { TimestampFromFlag = "2026-09-02T00:00:00Z", TimestampToFlag = "2026-09-01T00:00:00Z" }
            .Validate().ShouldBe("--timestamp-from must be less than or equal to --timestamp-to");

        new EventQueryInput { SequenceFloorFlag = 10, SequenceCeilingFlag = 5 }
            .Validate().ShouldBe("--sequence-floor must be less than or equal to --sequence-ceiling");
    }

    [Fact]
    public void half_open_windows_are_valid()
    {
        new EventQueryInput { TimestampFromFlag = "2026-09-01T00:00:00Z" }.Validate().ShouldBeNull();
        new EventQueryInput { SequenceCeilingFlag = 100 }.Validate().ShouldBeNull();
    }

    [Fact]
    public void the_command_name_is_kebab_cased()
    {
        // CommandFactory only strips the "Command" suffix and lowercases, so without the explicit
        // name this would register as "eventquery".
        var attribute = typeof(EventQueryCommand)
            .GetCustomAttributes(typeof(JasperFx.CommandLine.DescriptionAttribute), false)
            .Cast<JasperFx.CommandLine.DescriptionAttribute>()
            .Single();

        attribute.Name.ShouldBe("event-query");
    }
}
