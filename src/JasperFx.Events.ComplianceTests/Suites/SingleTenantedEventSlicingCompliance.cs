using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

public record TallyIncremented(int Amount);

/// <summary>
/// A plain self-aggregating snapshot for the single-tenanted slicing suite. Deliberately additive,
/// so a stream folded once and a stream folded twice produce different numbers rather than the same
/// one by luck.
/// </summary>
public partial class TenantSlicingTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
    public int EventCount { get; set; }

    public void Apply(TallyIncremented e)
    {
        Total += e.Amount;
        EventCount++;
    }
}

/// <summary>
/// On a <b>single-tenanted</b> event store, events whose <c>tenant_id</c> values disagree must still
/// fold into one aggregate. Slicing them per tenant splits one stream into several partial documents,
/// each having seen only part of its own history.
/// </summary>
/// <remarks>
/// <para>
/// This is wolverine#2053, transferred to
/// <see href="https://github.com/JasperFx/marten/issues/4085" />: a single-tenanted store whose event
/// rows carried a mix of tenant ids, written by a client stamping appends inconsistently. The reported
/// symptom was an <c>Apply</c> receiving a document with every property at its default, as though
/// <c>Create</c> had never run. Only the <b>async daemon</b> was affected — live and inline aggregation
/// fold the same events correctly — which is why this suite drives the daemon rather than asserting on
/// a live aggregate.
/// </para>
/// <para>
/// It is a compliance suite rather than three local tests because the fix reached exactly one store.
/// JasperFx.Events carries <c>ForceSingleTenancy</c> on <c>TenantedEventSlicer</c>; Marten set it by
/// overriding <c>BuildSlicer</c>, while Polecat's and Fisher's <c>SingleStreamProjection&lt;TDoc,TId&gt;</c>
/// are empty class bodies that never did. <see href="https://github.com/JasperFx/jasperfx/issues/723" />
/// moves the decision into the shared base, and this suite is what holds every store to it.
/// See <see href="https://github.com/JasperFx/jasperfx/issues/724" />.
/// </para>
/// <para>
/// <b>Opt-in.</b> Unlike the facts that ride along inside an already-enrolled suite, this one is
/// enrolled deliberately, because the precondition it needs is unusual and a store should adopt it
/// knowingly rather than discover it in a version bump.
/// </para>
/// </remarks>
public abstract class SingleTenantedEventSlicingCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_single_tenanted_slicing";

        config.AddEventType<TallyIncremented>();

        // No ConjoinedEventTenancy: the store stays on its default single-tenanted event store,
        // which is the entire precondition under test.
        config.Snapshot<TenantSlicingTally>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task async_projection_folds_one_stream_despite_disagreeing_tenant_ids()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            var events = EventsFor(session);

            // Stamp the envelopes directly. IEvent.TenantId is settable on the shared interface, which
            // is what lets a suite reproduce the mixed-tenancy rows the bug report described without
            // reaching for any store's own append API.
            var first = events.BuildEvent(new TallyIncremented(1));
            first.TenantId = StorageConstants.DefaultTenantId;

            var second = events.BuildEvent(new TallyIncremented(2));
            second.TenantId = "some-other-tenant";

            var third = events.BuildEvent(new TallyIncremented(4));
            third.TenantId = "some-other-tenant";

            events.Append(streamId, first, second, third);
            await SaveChangesAsync(session);
        }

        await SkipUnlessTheStorePersistedDisagreeingTenantIdsAsync(streamId);

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();
        var tally = await LoadDocumentAsync<TenantSlicingTally>(query, streamId);

        tally.ShouldNotBeNull();

        // The load-bearing assertion. A store slicing per tenant writes a document that saw only the
        // events of whichever tenant group landed last, so both numbers come up short rather than
        // wrong in some exotic way.
        tally.EventCount.ShouldBe(3);
        tally.Total.ShouldBe(7);
    }

    /// <summary>
    /// Skip rather than pass when the store normalized the tenant ids away on write.
    /// </summary>
    /// <remarks>
    /// Without this the suite is worthless on exactly the stores it cannot test: a store that rewrites
    /// every event's tenant id to the default on a single-tenanted store never had disagreeing rows to
    /// mis-slice, so it satisfies the assertion above for a reason that has nothing to do with the
    /// behavior under test. Vacuous green is worse than a skip, because only one of the two is visible.
    /// Same reasoning as the recorded hit count in <c>AggregateWriteCacheCompliance</c>.
    /// </remarks>
    private async Task SkipUnlessTheStorePersistedDisagreeingTenantIdsAsync(Guid streamId)
    {
        await using var session = OpenSession();
        var stored = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);

        var distinctTenants = stored.Select(x => x.TenantId).Distinct().Count();

        Assert.SkipWhen(distinctTenants < 2,
            $"This event store normalized the appended tenant ids down to {distinctTenants} distinct value(s) " +
            "on a single-tenanted store, so the mixed-tenancy precondition this suite needs cannot be " +
            "constructed through the shared surface. The behavior is not being asserted -- see jasperfx#724.");
    }
}
