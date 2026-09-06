using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Stream query plan events

public record ExpeditionStarted(string Name);

public record ExplorerJoined(string Explorer);

#endregion

/// <summary>
/// The two stream-fetch query plans — a stream <em>state</em> plan and a raw <em>event</em> plan —
/// executed standalone and inside a batched query.
/// </summary>
/// <remarks>
/// <para>
/// Both products ship a file literally named <c>fetching_stream_query_plans.cs</c> asserting the
/// same six behaviours, which is what marks this as shared rather than product-owned. The plan types
/// themselves are not shared and cannot be: each store declares its own <c>IQueryPlan&lt;T&gt;</c>
/// and <c>IBatchQueryPlan&lt;T&gt;</c>, and a plan is bound to the store's own reader. So the suite
/// costs two seam members — <c>FetchStreamStateByPlanAsync</c> and <c>FetchStreamByPlanAsync</c> —
/// and asserts against the shared <see cref="StreamState"/> and <see cref="IEvent"/> results.
/// </para>
/// <para>
/// The <c>batched</c> axis is the point of the suite rather than thoroughness for its own sake. A
/// plan implements two interfaces with two separate implementations: standalone it owns the whole
/// command, batched it contributes a fragment to someone else's. Every behaviour is therefore
/// asserted twice, and the version cap — the one parameter that has to survive into the batched
/// fragment's own SQL — is the one most likely to drift between the two.
/// </para>
/// <para>
/// Deliberately out of scope: proving that several plans genuinely share <em>one</em> round trip.
/// Both products' local tests batch the two plans alongside a document load and assert all three
/// answer, but round-trip counting is not observable through any shared surface, and the fixture
/// member here takes one plan at a time on purpose — a seam that accepted a plan list would be
/// re-implementing each store's batch API rather than reaching it. What is portable is that a plan
/// executed <em>through</em> the batched path answers identically to the standalone one, and that is
/// what every fact below asserts twice.
/// </para>
/// </remarks>
public abstract class StreamQueryPlanCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_stream_plans";
        config.AddEventType<ExpeditionStarted>();
        config.AddEventType<ExplorerJoined>();
    };

    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_stream_plans_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.AddEventType<ExpeditionStarted>();
        config.AddEventType<ExplorerJoined>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void assertSupported() => Assert.SkipUnless(theFixture.SupportsStreamQueryPlans,
        "This event store does not ship the stream fetch query plans");

    private async Task<Guid> anExpeditionAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId,
            new ExpeditionStarted("Erebor"),
            new ExplorerJoined("Bilbo"),
            new ExplorerJoined("Thorin"));
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<string> anExpeditionByKeyAsync()
    {
        var key = $"expedition/{Guid.NewGuid():N}";

        await using var session = OpenSession();
        EventsFor(session).StartStream(key,
            new ExpeditionStarted("Erebor"),
            new ExplorerJoined("Bilbo"),
            new ExplorerJoined("Thorin"));
        await SaveChangesAsync(session);

        return key;
    }

    private Task<StreamState?> stateByPlanAsync(TQuerySession session, object identity, bool batched)
        => theFixture.FetchStreamStateByPlanAsync(session, identity, batched, Cancellation);

    private Task<IReadOnlyList<IEvent>> eventsByPlanAsync(
        TQuerySession session, object identity, bool batched, long version = 0)
        => theFixture.FetchStreamByPlanAsync(session, identity, version, batched, Cancellation);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_stream_state_plan_returns_the_stream_state(bool batched)
    {
        assertSupported();

        var streamId = await anExpeditionAsync();

        await using var session = OpenSession();
        var state = await stateByPlanAsync(session, streamId, batched);

        state.ShouldNotBeNull();
        state.Id.ShouldBe(streamId);
        state.Version.ShouldBe(3);
        state.IsArchived.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_stream_state_plan_is_null_for_a_stream_that_does_not_exist(bool batched)
    {
        assertSupported();

        await using var session = OpenSession();
        var state = await stateByPlanAsync(session, Guid.NewGuid(), batched);

        state.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_stream_plan_returns_the_events_in_version_order(bool batched)
    {
        assertSupported();

        var streamId = await anExpeditionAsync();

        await using var session = OpenSession();
        var events = await eventsByPlanAsync(session, streamId, batched);

        events.Count.ShouldBe(3);
        events.Select(x => x.Version).ShouldBe(new long[] { 1, 2, 3 });
        events[0].Data.ShouldBeOfType<ExpeditionStarted>().Name.ShouldBe("Erebor");
        events.ShouldAllBe(x => x.StreamId == streamId);
    }

    /// <summary>
    /// The parameter most likely to survive standalone and be dropped in the batched fragment, since
    /// the batched item composes its own SQL.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_stream_plan_honours_the_version_cap(bool batched)
    {
        assertSupported();

        var streamId = await anExpeditionAsync();

        await using var session = OpenSession();
        var events = await eventsByPlanAsync(session, streamId, batched, version: 2);

        events.Count.ShouldBe(2);
        events[^1].Version.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_stream_plan_is_empty_for_a_stream_that_does_not_exist(bool batched)
    {
        assertSupported();

        await using var session = OpenSession();
        var events = await eventsByPlanAsync(session, Guid.NewGuid(), batched);

        events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_plans_work_against_string_stream_identity(bool batched)
    {
        assertSupported();

        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await anExpeditionByKeyAsync();

        await using var session = OpenSession();

        var state = await stateByPlanAsync(session, key, batched);
        state.ShouldNotBeNull();
        state.Key.ShouldBe(key);
        state.Version.ShouldBe(3);

        var events = await eventsByPlanAsync(session, key, batched);
        events.Count.ShouldBe(3);
        events.ShouldAllBe(x => x.StreamKey == key);
    }

    /// <summary>
    /// The plans read; they must not write. Running one inside a session with queued, uncommitted
    /// work leaves that work queued.
    /// </summary>
    [Fact]
    public async Task running_a_plan_does_not_commit_pending_session_work()
    {
        assertSupported();

        var streamId = await anExpeditionAsync();

        await using var session = OpenSession();
        EventsFor(session).Append(streamId, new ExplorerJoined("Balin"));

        var state = await stateByPlanAsync(session, streamId, batched: false);

        // The plan reads what is persisted, and the pending append is still pending.
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
    }
}
