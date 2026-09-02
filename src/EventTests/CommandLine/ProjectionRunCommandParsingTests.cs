using System.Collections.Generic;
using JasperFx.CommandLine;
using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#728 — the command line itself. The parser derives one-letter aliases from a flag's first
/// letter and has NO collision detection, so <c>--stream</c>/<c>--store</c> and <c>--to</c>/<c>--tenant</c>
/// would silently bind whichever handler was built first. Every flag is long-form only for that
/// reason, and these tests are what would notice if one lost its <c>longAliasOnly</c>.
/// </summary>
public class ProjectionRunCommandParsingTests
{
    private static ProjectionRunInput parse(params string[] args)
    {
        var command = new ProjectionRunCommand();
        return (ProjectionRunInput)command.Usages.BuildInput(new Queue<string>(args), new ActivatorCommandCreator());
    }

    [Fact]
    public void the_command_is_named_projection_run()
    {
        // CommandFactory.CommandNameFor strips the "Command" suffix and lowercases; it does NOT
        // split PascalCase. Without an explicit Name this is "projectionrun", which is what
        // shipped in 2.60.0 — a one-word sibling (ProjectionsCommand) is why it went unnoticed.
        CommandFactory.CommandNameFor(typeof(ProjectionRunCommand)).ShouldBe("projection-run");
    }

    [Fact]
    public void the_projection_name_is_the_argument()
    {
        parse("Trips", "--stream", "trip-1").ProjectionName.ShouldBe("Trips");
    }

    [Fact]
    public void stream_and_store_do_not_collide()
    {
        var input = parse("Trips", "--stream", "trip-1", "--store", "marten://main");

        input.StreamFlag.ShouldBe("trip-1");
        input.StoreFlag.ShouldBe("marten://main");
    }

    [Fact]
    public void to_and_tenant_do_not_collide()
    {
        var input = parse("Trips", "--stream", "trip-1", "--from", "2", "--to", "5", "--tenant", "acme");

        input.FromFlag.ShouldBe(2);
        input.ToFlag.ShouldBe(5);
        input.TenantFlag.ShouldBe("acme");
        input.SourceMode.ShouldBe(ProjectionRunSourceMode.StreamSlice);
    }

    [Fact]
    public void tags_are_repeatable_pairs()
    {
        var input = parse("Enrollments", "--tag:course", "c-1", "--tag:student", "s-9");

        input.TagFlag["course"].ShouldBe("c-1");
        input.TagFlag["student"].ShouldBe("s-9");
        input.SourceMode.ShouldBe(ProjectionRunSourceMode.TagQuery);
    }

    [Fact]
    public void json_is_a_boolean_flag()
    {
        parse("Trips", "--stream", "trip-1", "--json").JsonFlag.ShouldBeTrue();
        parse("Trips", "--stream", "trip-1").JsonFlag.ShouldBeFalse();
    }

    [Fact]
    public void the_event_cap_defaults_to_a_thousand()
    {
        parse("Trips", "--stream", "trip-1").MaxEventsFlag.ShouldBe(1000);
        parse("Trips", "--stream", "trip-1", "--max-events", "25").MaxEventsFlag.ShouldBe(25);
    }
}
