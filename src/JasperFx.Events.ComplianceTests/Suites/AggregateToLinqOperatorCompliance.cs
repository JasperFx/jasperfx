using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <c>AggregateToAsync&lt;T&gt;</c> — folding every event matched by an ad hoc event query into a
/// single aggregate, regardless of stream, optionally starting from supplied state, with the
/// aggregate's identity stamped from the last matched event's stream.
/// </summary>
/// <remarks>
/// <para>
/// The single-aggregate twin of
/// <see cref="AggregateToManyCompliance{TFixture,TOperations,TQuerySession}"/>, and it shares that
/// suite's seam and <c>SupportsAggregateToLinqOperators</c> gate. It deliberately folds
/// <see cref="ComplianceTrail"/> — the same unregistered self-aggregating type the live aggregation
/// suite uses — so the operator is proven over the store's derived live aggregator, the common
/// ground both products share, rather than anything registered.
/// </para>
/// <para>
/// The two identity facts are the ones a store is most tempted to get wrong: identity is stamped
/// from the <em>stream</em> of the matched events (Guid or string per the store's stream identity),
/// not from anything the fold itself computed.
/// </para>
/// </remarks>
public abstract class AggregateToLinqOperatorCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_aggregate_to";

        config.AddEventType<TrailStarted>();
        config.AddEventType<MilesWalked>();
    };

    /// <summary>
    /// String stream identity, for the key-stamping fact. Uses the string identity suite's
    /// self-aggregating quest so nothing here needs registration either.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_aggregate_to_string";
        config.StreamIdentity = StreamIdentity.AsString;

        config.AddEventType<StringQuestStarted>();
        config.AddEventType<StringMembersJoined>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAggregateToLinqOperators,
            "This event store has not implemented the AggregateTo LINQ operators under test");
    }

    [Fact]
    public async Task can_aggregate_events_to_aggregate_type_asynchronously()
    {
        SkipUnlessSupported();

        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(stream1, new TrailStarted("Long Trail"), new MilesWalked(10));
        EventsFor(session).StartStream(stream2, new MilesWalked(5), new MilesWalked(7));
        await SaveChangesAsync(session);

        // No filter: every event in the store folds into one aggregate, across both streams.
        var trail = await theFixture.AggregateEventsToAsync<ComplianceTrail>(session, null, null, Cancellation);

        trail.ShouldNotBeNull();
        trail.Name.ShouldBe("Long Trail");
        trail.Miles.ShouldBe(22);
    }

    [Fact]
    public async Task can_aggregate_with_initial_state_asynchronously()
    {
        SkipUnlessSupported();

        var stream = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(stream, new TrailStarted("Long Trail"), new MilesWalked(10));
        await SaveChangesAsync(session);

        var initial = new ComplianceTrail { Miles = 100 };
        var trail = await theFixture.AggregateEventsToAsync(session,
            e => e.StreamId == stream, initial, Cancellation);

        trail.ShouldNotBeNull();
        trail.Name.ShouldBe("Long Trail");
        trail.Miles.ShouldBe(110);
    }

    [Fact]
    public async Task returns_null_when_the_query_matches_no_events()
    {
        SkipUnlessSupported();

        await using var session = OpenSession();

        var nothing = Guid.NewGuid(); // matches no stream
        var trail = await theFixture.AggregateEventsToAsync<ComplianceTrail>(session,
            e => e.StreamId == nothing, null, Cancellation);

        trail.ShouldBeNull();
    }

    [Fact]
    public async Task gets_the_id_set()
    {
        SkipUnlessSupported();

        var stream1 = Guid.NewGuid();
        var stream2 = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(stream1, new TrailStarted("Long Trail"), new MilesWalked(10));
        EventsFor(session).StartStream(stream2, new TrailStarted("Other"), new MilesWalked(5));
        await SaveChangesAsync(session);

        var trail = await theFixture.AggregateEventsToAsync<ComplianceTrail>(session,
            e => e.StreamId == stream1, null, Cancellation);

        trail.ShouldNotBeNull();
        trail.Id.ShouldBe(stream1);
    }

    [Fact]
    public async Task gets_the_key_set()
    {
        SkipUnlessSupported();

        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = Guid.NewGuid().ToString();

        await using var session = OpenSession();
        EventsFor(session).StartStream(key,
            new StringQuestStarted("Save the World"),
            new StringMembersJoined(1, "Emond's Field", ["Rand", "Matrim"]));
        EventsFor(session).StartStream(Guid.NewGuid().ToString(),
            new StringQuestStarted("Other"),
            new StringMembersJoined(2, "Tar Valon", ["Elayne"]));
        await SaveChangesAsync(session);

        var quest = await theFixture.AggregateEventsToAsync<SelfAggregatingStringQuest>(session,
            e => e.StreamKey == key, null, Cancellation);

        quest.ShouldNotBeNull();
        quest.Id.ShouldBe(key);
        quest.Name.ShouldBe("Save the World");
        quest.Members.ShouldBe(new[] { "Rand", "Matrim" });
    }
}
