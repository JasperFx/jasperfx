using JasperFx.Events.Aggregation;
using Shouldly;

namespace EventTests.Projections;

/// <summary>
/// jasperfx#887 — "No source-generated dispatcher found" had to cover every cause at once, because the
/// runtime could not tell "the generator never ran in that assembly" (a csproj problem) from "the
/// generator ran and declined your type" (a code-shape problem). These pin both halves of the marker
/// that decides between them.
/// </summary>
public class SourceGeneratorMarkerTests
{
    // End-to-end, through a real build: this test project references the analyzer, so its own assembly
    // carries the marker the generator emits. Nothing else here would catch the marker being emitted
    // into a file the compiler never picks up.
    [Fact]
    public void this_assembly_confirms_the_generator_ran()
    {
        SourceGeneratorMarker.EvidenceFor(GetType().Assembly)
            .ShouldBe(SourceGeneratorEvidence.Confirmed);
    }

    [Fact]
    public void an_assembly_the_generator_never_touched_is_unconfirmed()
    {
        SourceGeneratorMarker.EvidenceFor(typeof(string).Assembly)
            .ShouldBe(SourceGeneratorEvidence.Unconfirmed);
    }

    [Fact]
    public void a_confirmed_assembly_gets_the_code_shape_explanation()
    {
        var message = SourceGeneratorMarker.DescribeGeneratorReach([GetType().Assembly], "the aggregate Foo");

        message.ShouldContain("DID run");
        message.ShouldContain("declined it");
        message.ShouldContain("JFXEVT003");

        // The opposite fix must not be suggested alongside it — saying both is the defect.
        message.ShouldNotContain("ExcludeAssets");
    }

    [Fact]
    public void an_unconfirmed_assembly_gets_the_build_configuration_explanation()
    {
        var message = SourceGeneratorMarker.DescribeGeneratorReach([typeof(string).Assembly], "the aggregate Foo");

        message.ShouldContain("never ran there");
        message.ShouldContain("ExcludeAssets");
        message.ShouldContain("PrivateAssets");

        // And it says so without asserting more than it knows: an older generator leaves no marker
        // either, so the reader is told how to rule that out.
        message.ShouldContain("older than this marker");
        message.ShouldNotContain("JFXEVT003");
    }

    [Fact]
    public void confirmation_in_either_scanned_assembly_is_enough()
    {
        // The runtime scans the aggregate's assembly AND the projection's, so a projection-specific
        // evolver emitted into the projection's assembly counts as the generator having run.
        var message = SourceGeneratorMarker.DescribeGeneratorReach(
            [typeof(string).Assembly, GetType().Assembly], "the aggregate Foo");

        message.ShouldContain("DID run");
        message.ShouldContain("System.Private.CoreLib");
        message.ShouldContain("EventTests");
    }

    [Fact]
    public void the_missing_dispatcher_message_names_the_aggregate_and_picks_one_cause()
    {
        // TAggregate from an assembly with no marker: the message must land on the build-configuration
        // half and name the assembly the user has to fix.
        var message = new AggregateApplication<string, FakeSession>().MissingDispatcherMessage();

        message.ShouldContain("No source-generated dispatcher found for string");
        message.ShouldContain("System.Private.CoreLib");
        message.ShouldContain("ExcludeAssets");
        message.ShouldNotContain("JFXEVT003");
    }
}
