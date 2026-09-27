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

    #region jasperfx#906 — which assemblies count as evidence

    /// <summary>
    /// A framework-owned closed generic contributes no evidence (jasperfx#906).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this pins is an <em>inverted verdict</em>, not a wording problem. A projection registered
    /// as <c>Snapshot&lt;T&gt;()</c> / <c>SingleStreamProjection&lt;T, TId&gt;</c> /
    /// <c>AggregateStream&lt;T&gt;</c> has a framework-owned projection type whose assembly is the
    /// store's own — <c>Marten.dll</c>, or here <c>JasperFx.Events.dll</c> — and the store's build runs
    /// the generator, so that assembly always carries the marker. Admitting it pinned the verdict to
    /// "the generator ran" for the commonest registration shape in the product, and the message then
    /// told the reader that the cause they actually had was explicitly not the cause. marten#5495 is
    /// exactly that shape, and jasperfx#887 was filed out of it — so as shipped it would have
    /// misdirected the very reporter it was written for.
    /// </para>
    /// <para>
    /// <see cref="JasperFxSourceGeneratorAppliedAttribute" /> lives in <c>JasperFx.Events</c>, whose own
    /// build runs the generator, so that assembly stands in for the store's here: it is a real assembly
    /// that really does carry the marker, which is what makes this a fair reproduction rather than a
    /// mock.
    /// </para>
    /// </remarks>
    [Fact]
    public void a_framework_owned_closed_generic_contributes_no_evidence()
    {
        // Stands in for Marten's SingleStreamProjection<App.MyAggregate, Guid>: a closed generic whose
        // definition lives in the framework, over an aggregate from a marker-less assembly.
        var frameworkGeneric = typeof(AggregateApplication<string, FakeSession>);

        frameworkGeneric.IsConstructedGenericType.ShouldBeTrue("the arrangement depends on this");
        SourceGeneratorMarker.EvidenceFor(frameworkGeneric.Assembly)
            .ShouldBe(SourceGeneratorEvidence.Confirmed,
                "JasperFx.Events must really carry the marker, or this fact proves nothing");

        var assemblies = AggregateApplication<string, FakeSession>
            .EvidenceAssembliesFor(typeof(string), frameworkGeneric);

        assemblies.ShouldHaveSingleItem().ShouldBeSameAs(typeof(string).Assembly);
    }

    /// <summary>
    /// The end-to-end consequence: the verdict lands on the build-configuration half.
    /// </summary>
    /// <remarks>
    /// The assembly-selection fact above is the mechanism; this is what a user reads. Kept separate
    /// because a future change could keep the list right and still reach the wrong branch.
    /// </remarks>
    [Fact]
    public void an_aggregate_in_a_markerless_assembly_reports_the_csproj_cause_through_a_framework_generic()
    {
        var message = SourceGeneratorMarker.DescribeGeneratorReach(
            AggregateApplication<string, FakeSession>.EvidenceAssembliesFor(
                typeof(string), typeof(AggregateApplication<string, FakeSession>)),
            "the aggregate string");

        message.ShouldContain("never ran there");
        message.ShouldContain("ExcludeAssets");

        // The inversion, stated as the thing that must not happen.
        message.ShouldNotContain("DID run");
        message.ShouldNotContain("declined it");
    }

    /// <summary>
    /// The control, and the reason the fix is not <c>Any</c> → <c>All</c>: a user-declared subclass in
    /// another assembly still contributes evidence.
    /// </summary>
    /// <remarks>
    /// The generator emits a projection-specific evolver into a declared subclass's own assembly, so
    /// confirmation there is genuine even when the aggregate's assembly carries no marker. A fix that
    /// changed the quantifier, or excluded the projection assembly outright, would pass the two facts
    /// above and break this one.
    /// </remarks>
    [Fact]
    public void a_declared_projection_subclass_in_another_assembly_still_contributes_evidence()
    {
        // Not a constructed generic and not in the aggregate's assembly — the shape that must survive.
        var declared = typeof(DeclaredProjectionStandIn);

        declared.IsConstructedGenericType.ShouldBeFalse("the arrangement depends on this");

        var assemblies = AggregateApplication<string, FakeSession>
            .EvidenceAssembliesFor(typeof(string), declared);

        assemblies.Count.ShouldBe(2);
        assemblies.ShouldContain(typeof(string).Assembly);
        assemblies.ShouldContain(declared.Assembly);
    }

    /// <summary>
    /// A projection type in the aggregate's own assembly is not listed twice.
    /// </summary>
    [Fact]
    public void a_projection_in_the_aggregates_own_assembly_is_listed_once()
    {
        var assemblies = AggregateApplication<DeclaredProjectionStandIn, FakeSession>
            .EvidenceAssembliesFor(typeof(DeclaredProjectionStandIn), typeof(DeclaredProjectionStandIn));

        assemblies.ShouldHaveSingleItem().ShouldBeSameAs(typeof(DeclaredProjectionStandIn).Assembly);
    }

    /// <summary>
    /// Stands in for a user-declared <c>MyProjection : SingleStreamProjection&lt;Agg, Guid&gt;</c> —
    /// non-generic, declared in a different assembly from the aggregate under test (<c>string</c>).
    /// </summary>
    private class DeclaredProjectionStandIn;

    #endregion
}
