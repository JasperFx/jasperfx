using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

public record CompositeCounted(int Amount);

/// <summary>
/// Stage-1 member of the compliance composite. Additive on purpose: a member that was replayed over
/// its surviving rows rather than torn down first reads back doubled, which a "last write wins"
/// aggregate would hide.
/// </summary>
public partial class CompositeFirstStageTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
    public int EventCount { get; set; }

    public void Apply(CompositeCounted e)
    {
        Total += e.Amount;
        EventCount++;
    }
}

/// <summary>
/// Stage-2 member. Identical shape to the stage-1 member by design — the fact under test is that a
/// later stage runs at all and sees the same batch, not that it transforms anything differently.
/// </summary>
public partial class CompositeSecondStageTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
    public int EventCount { get; set; }

    public void Apply(CompositeCounted e)
    {
        Total += e.Amount;
        EventCount++;
    }
}

/// <summary>
/// Composite projections: several members sharing one shard, one progression row and one event batch,
/// executed in stage order, and torn down together on rebuild.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour here is shared but the implementation is not: each product owns member teardown on
/// rebuild — Marten, Polecat's <c>DocumentStore.EventStore.cs</c>, Fisher's
/// <c>FisherCompositeProjection</c> / <c>CompositeIProjectionSource</c>. Two of the three already carry
/// a <em>local</em> regression test named for the same class of bug (<c>Bug_439_composite_member_teardown</c>,
/// <c>composite_member_teardown</c>), which is the usual sign that the behaviour belongs in the shared
/// suite rather than three times over. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/725" />.
/// </para>
/// <para>
/// It also gives <see href="https://github.com/JasperFx/jasperfx/issues/684" /> — letting unchanged
/// members keep their tables across a version bump — a baseline to diff against. That is research
/// rather than a scoped feature today, and this suite is deliberately independent of it: everything
/// asserted here is current, shipped behaviour.
/// </para>
/// <para>
/// <b>Opt-in.</b> <see cref="IComplianceStoreRegistrar.AddCompositeProjection" /> carries a throwing
/// default, so a store without composites simply does not enroll.
/// </para>
/// <para>
/// Deliberately <b>no cross-stage enrichment</b>. A stage-2 member reading stage-1 output through
/// <c>EnrichWith&lt;T&gt;</c> is a real capability, but it is expressed by an imperative call inside a
/// member's slicing code rather than by anything a store-neutral config can describe, so a portable
/// fact cannot set it up. What is portable — and what actually broke in the products — is that every
/// member runs off one batch and is torn down as a unit.
/// </para>
/// </remarks>
public abstract class CompositeProjectionCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    public const string CompositeName = "ComplianceComposite";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_composite";

        config.AddEventType<CompositeCounted>();

        config.AddCompositeProjection(CompositeName, composite =>
        {
            composite.Snapshot<CompositeFirstStageTally>(1);
            composite.Snapshot<CompositeSecondStageTally>(2);
        });
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private void SkipUnlessDaemonIsSupported()
    {
        // Composites are async-only -- CompositeProjection.BuildForInline throws -- so a store without
        // a daemon has no way to run one at all.
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon, which a composite requires");
    }

    private async Task<Guid> AppendThreeEventsAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = EventsFor(session);
        events.StartStream<CompositeFirstStageTally>(streamId,
            new CompositeCounted(1), new CompositeCounted(2), new CompositeCounted(4));
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task every_stage_materializes_from_one_async_pass()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await AppendThreeEventsAsync();

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();

        var first = await LoadDocumentAsync<CompositeFirstStageTally>(query, streamId);
        first.ShouldNotBeNull();
        first.EventCount.ShouldBe(3);
        first.Total.ShouldBe(7);

        // The stage-2 member is the one a store can silently drop: the composite's single shard reports
        // itself caught up either way, so nothing else notices that a later stage never ran.
        var second = await LoadDocumentAsync<CompositeSecondStageTally>(query, streamId);
        second.ShouldNotBeNull();
        second.EventCount.ShouldBe(3);
        second.Total.ShouldBe(7);
    }

    [Fact]
    public async Task rebuilding_the_composite_tears_members_down_rather_than_replaying_over_them()
    {
        SkipUnlessDaemonIsSupported();

        var streamId = await AppendThreeEventsAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync(CompositeName, Cancellation);

        await using var query = OpenSession();

        // Both members are additive, so a store that replayed the stream over surviving rows instead of
        // tearing the member down reads back exactly doubled. That is the failure both products already
        // guard locally, and the reason this suite exists.
        var first = await LoadDocumentAsync<CompositeFirstStageTally>(query, streamId);
        first.ShouldNotBeNull();
        first.EventCount.ShouldBe(3);
        first.Total.ShouldBe(7);

        var second = await LoadDocumentAsync<CompositeSecondStageTally>(query, streamId);
        second.ShouldNotBeNull();
        second.EventCount.ShouldBe(3);
        second.Total.ShouldBe(7);
    }

    [Fact]
    public void a_composite_presents_itself_as_exactly_one_shard()
    {
        // Everything else here depends on this: progression, rebuild and teardown all key off a single
        // shard name composed from the composite's name and version, which is what makes the members
        // advance in lockstep. A store that expanded a composite into one shard per member would pass
        // both facts above and still have changed what a composite is.
        var store = theFixture.EventStore.ShouldBeAssignableTo<IEventStore<TOperations, TQuerySession>>();

        var shards = store.AllShards().Where(x => x.Name.Name == CompositeName).ToArray();

        shards.Length.ShouldBe(1);
    }
}
