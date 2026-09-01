using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#728 — the report is the contract <c>--json</c> consumers parse, so its shape is pinned
/// here rather than left to whatever the renderer happens to emit.
/// </summary>
public class ProjectionRunReportTests
{
    private static JsonElement json(string text) => JsonDocument.Parse(text).RootElement;

    private static EventRecord evt(long version, string type) => new(
        Guid.NewGuid(), 100 + version, version, "trip-1", type, json("{}"), null,
        new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), null, null);

    private static ProjectionTimelineRaw timeline(params ProjectionStepResultRaw[] steps)
        => new(steps, steps.LastOrDefault()?.After);

    private static ProjectionRunInput theInput
        => new() { ProjectionName = "Trips", StreamFlag = "trip-1" };

    [Fact]
    public void identities_are_ordered_so_two_runs_of_a_slice_diff_cleanly()
    {
        // MultiAggregateProjectionResult carries a dictionary and promises no order.
        var result = new MultiAggregateProjectionResult("Trips", new Dictionary<string, ProjectionTimelineRaw>
        {
            ["trip-9"] = timeline(),
            ["trip-1"] = timeline(),
            ["trip-4"] = timeline()
        });

        var report = ProjectionRunReport.From(theInput, new Uri("marten://main"), result, 3, false);

        report.Aggregates.Select(x => x.Identity).ShouldBe(["trip-1", "trip-4", "trip-9"]);
    }

    [Fact]
    public void steps_are_numbered_from_one_in_apply_order()
    {
        var result = new MultiAggregateProjectionResult("Trips", new Dictionary<string, ProjectionTimelineRaw>
        {
            ["trip-1"] = timeline(
                new ProjectionStepResultRaw(evt(1, "TripStarted"), null, json("""{"legs":0}"""), TimeSpan.FromMilliseconds(2), null),
                new ProjectionStepResultRaw(evt(2, "LegAdded"), json("""{"legs":0}"""), json("""{"legs":1}"""), TimeSpan.FromMilliseconds(1), null))
        });

        var report = ProjectionRunReport.From(theInput, new Uri("marten://main"), result, 2, false);
        var steps = report.Aggregates.Single().Steps;

        steps.Select(x => x.Step).ShouldBe([1, 2]);
        steps.Select(x => x.EventType).ShouldBe(["TripStarted", "LegAdded"]);
        steps[0].StreamVersion.ShouldBe(1);
        steps[0].Sequence.ShouldBe(101);
        steps[0].ElapsedMs.ShouldBe(2);
    }

    [Fact]
    public void a_failed_apply_carries_its_message_onto_the_step()
    {
        var result = new MultiAggregateProjectionResult("Trips", new Dictionary<string, ProjectionTimelineRaw>
        {
            ["trip-1"] = timeline(new ProjectionStepResultRaw(
                evt(1, "TripStarted"), null, null, TimeSpan.Zero, "Object reference not set"))
        });

        var report = ProjectionRunReport.From(theInput, new Uri("marten://main"), result, 1, false);

        report.Aggregates.Single().Steps.Single().Error.ShouldBe("Object reference not set");
        report.Error.ShouldBeNull();
    }

    [Fact]
    public void a_failed_run_still_describes_the_slice_it_could_not_replay()
    {
        var input = theInput;
        input.FromFlag = 2;
        input.ToFlag = 5;

        var report = ProjectionRunReport.Failed(input, new Uri("marten://main"), "No such projection");

        report.Error.ShouldBe("No such projection");
        report.Aggregates.ShouldBeEmpty();
        report.EventCount.ShouldBe(0);
        report.Source.Mode.ShouldBe(ProjectionRunSourceMode.StreamSlice);
        report.Source.Key.ShouldBe("trip-1@2..5");
        report.Source.FromVersion.ShouldBe(2);
        report.Source.ToVersion.ShouldBe(5);
    }

    [Fact]
    public void a_tag_query_report_carries_the_tags_and_not_a_stream()
    {
        var input = new ProjectionRunInput { ProjectionName = "Enrollments" };
        input.TagFlag["course"] = "c-1";

        var source = ProjectionRunSourceReport.From(input);

        source.Mode.ShouldBe(ProjectionRunSourceMode.TagQuery);
        source.StreamId.ShouldBeNull();
        source.Tags!["course"].ShouldBe("c-1");
    }

    [Fact]
    public void a_stream_report_carries_the_stream_and_not_tags()
    {
        var source = ProjectionRunSourceReport.From(theInput);

        source.Mode.ShouldBe(ProjectionRunSourceMode.Stream);
        source.StreamId.ShouldBe("trip-1");
        source.Tags.ShouldBeNull();
    }

    [Fact]
    public void the_truncation_flag_survives_into_the_report()
    {
        var result = new MultiAggregateProjectionResult("Trips", new Dictionary<string, ProjectionTimelineRaw>());

        ProjectionRunReport.From(theInput, new Uri("marten://main"), result, 1000, true)
            .Truncated.ShouldBeTrue();
    }

    [Fact]
    public void json_is_camel_cased_and_names_the_mode_as_a_string()
    {
        // Agents read this; a numeric enum would be an unlabelled 0/1/2.
        var input = theInput;
        input.FromFlag = 2;
        input.ToFlag = 5;

        var text = ProjectionRunView.ToJson(ProjectionRunReport.Failed(input, new Uri("marten://main"), "nope"));

        text.ShouldContain("\"projection\": \"Trips\"");
        text.ShouldContain("\"eventCount\": 0");
        text.ShouldContain("\"mode\": \"StreamSlice\"");
    }
}
