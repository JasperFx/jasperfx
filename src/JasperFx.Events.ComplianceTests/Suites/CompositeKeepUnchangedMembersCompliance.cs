using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>A member of the kept-members composite. Additive, so a member replayed over its rows reads back doubled.</summary>
public partial class KeptCompositeTally
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

/// <summary>The other member, with the same shape.</summary>
public partial class KeptCompositeOtherTally
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
/// jasperfx#684: with <c>KeepUnchangedMembers</c> on, a rebuild of a composite whose own version was bumped leaves
/// standing every member that kept its version, documents and progression row both, and a rebuild with no bump still
/// rebuilds every member.
/// </summary>
/// <remarks>
/// <para>
/// The member skip itself lives in JasperFx's composite execution and is covered there. What each store has to prove is
/// its half: teardown asks <c>CompositeProjection.MembersToTearDownAsync</c> which members to tear down, and leaves the
/// rest exactly as they are.
/// </para>
/// <para>
/// A bump is simulated by deleting the composite's own progression row, which is what a new composite version looks like
/// to the daemon: no row for the composite, and the members' rows still there for their unchanged versions.
/// </para>
/// </remarks>
[RequiresCapability("SupportsKeepingUnchangedCompositeMembers")]
public abstract class CompositeKeepUnchangedMembersCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    public const string CompositeName = "ComplianceKeptComposite";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_kept_composite";

        config.AddEventType<CompositeCounted>();

        // One stage: keeping members only applies to a single-stage composite
        config.AddCompositeProjection(CompositeName, composite =>
        {
            composite.Snapshot<KeptCompositeTally>(1);
            composite.Snapshot<KeptCompositeOtherTally>(1);
            composite.KeepUnchangedMembers();
        });
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon && theFixture.SupportsKeepingUnchangedCompositeMembers,
            "This event store does not keep unchanged composite members across a rebuild");
    }

    private async Task<Guid> ProjectThreeEventsAsync()
    {
        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<KeptCompositeTally>(streamId,
                new CompositeCounted(1), new CompositeCounted(2), new CompositeCounted(4));
            await SaveChangesAsync(session);
        }

        return streamId;
    }

    // A value no replay produces, so a member that was torn down and rebuilt reads back 7 instead
    private async Task MarkAsync(Guid streamId)
    {
        await using var session = OpenSession();
        StoreDocument(session, new KeptCompositeTally { Id = streamId, Total = 999, EventCount = 3 });
        StoreDocument(session, new KeptCompositeOtherTally { Id = streamId, Total = 999, EventCount = 3 });
        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task after_a_composite_bump_a_rebuild_leaves_the_unchanged_members_standing()
    {
        SkipUnlessSupported();

        var streamId = await ProjectThreeEventsAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);
        await StopDaemonAsync();

        await MarkAsync(streamId);

        var database = (await EventStore.AllDatabases()).First();
        await database.DeleteProjectionProgressByShardNameAsync(ShardName.Compose(CompositeName).Identity, Cancellation);

        daemon = await StartDaemonAsync();
        await daemon.RebuildProjectionAsync(CompositeName, Cancellation);

        await using var query = OpenSession();

        // Neither torn down (it would read 7) nor replayed over (it would read 1006)
        (await LoadDocumentAsync<KeptCompositeTally>(query, streamId))!.Total.ShouldBe(999);
        (await LoadDocumentAsync<KeptCompositeOtherTally>(query, streamId))!.Total.ShouldBe(999);
    }

    [Fact]
    public async Task a_rebuild_with_no_bump_rebuilds_every_member()
    {
        SkipUnlessSupported();

        var streamId = await ProjectThreeEventsAsync();

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await MarkAsync(streamId);

        await daemon.RebuildProjectionAsync(CompositeName, Cancellation);

        await using var query = OpenSession();

        // Every member and the composite were in lockstep, so this is a rebuild to repair data, and nothing is kept
        var first = await LoadDocumentAsync<KeptCompositeTally>(query, streamId);
        first.ShouldNotBeNull();
        first.Total.ShouldBe(7);
        first.EventCount.ShouldBe(3);

        var other = await LoadDocumentAsync<KeptCompositeOtherTally>(query, streamId);
        other.ShouldNotBeNull();
        other.Total.ShouldBe(7);
        other.EventCount.ShouldBe(3);
    }
}
