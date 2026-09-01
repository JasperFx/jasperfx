using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#728 — the source mode is derived from the flags rather than declared, and the per-mode
/// required-field rules deliberately mirror CritterWatch's <c>RequestProjectionRunHandler</c> so the
/// local CLI and the console's remote stepper refuse the same inputs for the same reasons.
/// </summary>
public class ProjectionRunInputTests
{
    private static ProjectionRunInput forStream(string stream = "trip-1")
        => new() { ProjectionName = "Trips", StreamFlag = stream };

    [Fact]
    public void a_bare_stream_read_is_stream_mode()
    {
        forStream().SourceMode.ShouldBe(ProjectionRunSourceMode.Stream);
    }

    [Fact]
    public void a_version_bound_makes_it_a_slice()
    {
        var input = forStream();
        input.FromFlag = 2;
        input.ToFlag = 5;

        input.SourceMode.ShouldBe(ProjectionRunSourceMode.StreamSlice);
    }

    [Fact]
    public void one_half_of_a_bound_is_still_a_slice_so_the_missing_half_can_be_reported()
    {
        // If a lone --from fell back to Stream mode the command would quietly replay the WHOLE
        // stream — an answer to a question nobody asked. It has to stay a slice in order to fail.
        var input = forStream();
        input.FromFlag = 2;

        input.SourceMode.ShouldBe(ProjectionRunSourceMode.StreamSlice);
        input.Validate().ShouldBe("--from and --to are both required for a stream slice");
    }

    [Fact]
    public void tags_win_over_everything()
    {
        var input = new ProjectionRunInput { ProjectionName = "Enrollments" };
        input.TagFlag["course"] = "c-1";

        input.SourceMode.ShouldBe(ProjectionRunSourceMode.TagQuery);
    }

    [Fact]
    public void a_projection_name_is_required()
    {
        new ProjectionRunInput { StreamFlag = "trip-1" }.Validate()
            .ShouldBe("A projection name is required");
    }

    [Fact]
    public void a_stream_is_required_for_a_stream_read()
    {
        new ProjectionRunInput { ProjectionName = "Trips" }.Validate().ShouldBe("--stream is required");
    }

    [Fact]
    public void a_stream_is_required_for_a_slice()
    {
        new ProjectionRunInput { ProjectionName = "Trips", FromFlag = 1, ToFlag = 2 }.Validate()
            .ShouldBe("--stream is required");
    }

    [Fact]
    public void an_inverted_slice_is_refused()
    {
        var input = forStream();
        input.FromFlag = 9;
        input.ToFlag = 4;

        input.Validate().ShouldBe("--from must be less than or equal to --to");
    }

    [Fact]
    public void a_single_version_slice_is_legal()
    {
        var input = forStream();
        input.FromFlag = 4;
        input.ToFlag = 4;

        input.Validate().ShouldBeNull();
    }

    [Fact]
    public void a_stream_cannot_be_combined_with_a_tag_query()
    {
        // CritterWatch's handler silently ignores the stream id in tag mode because its UI never
        // sends both. An operator typing both at a prompt means something, and the command cannot
        // honor it — so it says so rather than dropping half the request.
        var input = forStream();
        input.TagFlag["course"] = "c-1";

        input.Validate().ShouldBe("--stream cannot be combined with --tag; a tag query is not stream-anchored");
    }

    [Fact]
    public void version_bounds_cannot_be_combined_with_a_tag_query()
    {
        var input = new ProjectionRunInput { ProjectionName = "Enrollments", FromFlag = 1, ToFlag = 2 };
        input.TagFlag["course"] = "c-1";

        input.Validate()
            .ShouldBe("--from / --to cannot be combined with --tag; version bounds only apply to a stream slice");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void a_non_positive_event_cap_is_refused(int cap)
    {
        // Zero would read nothing and report an empty timeline, which is indistinguishable from a
        // stream that genuinely has no events.
        var input = forStream();
        input.MaxEventsFlag = cap;

        input.Validate().ShouldBe("--max-events must be greater than zero");
    }

    [Fact]
    public void source_key_for_a_stream_is_the_stream_id()
    {
        forStream().SourceKey.ShouldBe("trip-1");
    }

    [Fact]
    public void source_key_for_a_slice_carries_the_bounds()
    {
        var input = forStream();
        input.FromFlag = 2;
        input.ToFlag = 5;

        input.SourceKey.ShouldBe("trip-1@2..5");
    }

    [Fact]
    public void source_key_for_a_tag_query_is_order_independent()
    {
        // Two runs of the same query have to produce the same key, and a Dictionary does not promise
        // an enumeration order.
        var first = new ProjectionRunInput { ProjectionName = "Enrollments" };
        first.TagFlag["course"] = "c-1";
        first.TagFlag["student"] = "s-9";

        var second = new ProjectionRunInput { ProjectionName = "Enrollments" };
        second.TagFlag["student"] = "s-9";
        second.TagFlag["course"] = "c-1";

        first.SourceKey.ShouldBe("tags::course=c-1&student=s-9");
        second.SourceKey.ShouldBe(first.SourceKey);
    }
}
