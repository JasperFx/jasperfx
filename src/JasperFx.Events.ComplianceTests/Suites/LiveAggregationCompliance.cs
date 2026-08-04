using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Live aggregation events and aggregate

public record TrailStarted(string Name);

public record MilesWalked(int Miles);

public record TrailAbandoned;

/// <summary>
/// Folded on read only — never registered as a snapshot, so the suite exercises the store's live
/// aggregation path rather than a persisted document.
/// </summary>
/// <remarks>
/// <c>Apply(TrailAbandoned)</c> returns null, which is how both products spell "this aggregate is
/// deleted as of this event". That is what gives
/// <c>AggregateStreamToLastKnownAsync</c> something to walk back from.
/// </remarks>
public partial class ComplianceTrail
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Miles { get; set; }

    public static ComplianceTrail Create(TrailStarted e) => new() { Name = e.Name };

    public void Apply(MilesWalked e) => Miles += e.Miles;

    public ComplianceTrail? Apply(TrailAbandoned _) => null;
}

#endregion

/// <summary>
/// Live aggregation — folding a stream on read with nothing persisted — through
/// <c>AggregateStreamAsync</c> and <c>AggregateStreamToLastKnownAsync</c>, including the version and
/// timestamp bounds both accept.
/// </summary>
/// <remarks>
/// Nothing is registered here on purpose. Polecat derives live aggregators automatically and rejects
/// explicit registration (<see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsLiveAggregationRegistration"/>
/// is false there), so a suite that leans on <c>LiveAggregation&lt;T&gt;()</c> would need a gate it
/// does not actually need. Folding an unregistered type is the common ground.
/// </remarks>
public abstract class LiveAggregationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_live_aggregation";
        config.AddEventType<TrailStarted>();
        config.AddEventType<MilesWalked>();
        config.AddEventType<TrailAbandoned>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<Guid> aTrailAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceTrail>(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task aggregate_stream_folds_every_event()
    {
        var streamId = await aTrailAsync(
            new TrailStarted("Pennine Way"),
            new MilesWalked(12),
            new MilesWalked(9));

        await using var session = OpenSession();
        var trail = await EventsFor(session).AggregateStreamAsync<ComplianceTrail>(streamId, token: Cancellation);

        trail.ShouldNotBeNull();
        trail.Name.ShouldBe("Pennine Way");
        trail.Miles.ShouldBe(21);
    }

    [Fact]
    public async Task aggregate_stream_bounded_by_version()
    {
        var streamId = await aTrailAsync(
            new TrailStarted("Pennine Way"),
            new MilesWalked(12),
            new MilesWalked(9));

        await using var session = OpenSession();
        var trail = await EventsFor(session)
            .AggregateStreamAsync<ComplianceTrail>(streamId, 2, token: Cancellation);

        trail.ShouldNotBeNull();
        trail.Miles.ShouldBe(12);
    }

    [Fact]
    public async Task aggregate_stream_bounded_by_timestamp()
    {
        var streamId = await aTrailAsync(new TrailStarted("Pennine Way"), new MilesWalked(12));

        await Task.Delay(50, Cancellation);

        await using var writer = OpenSession();
        EventsFor(writer).Append(streamId, new MilesWalked(9));
        await SaveChangesAsync(writer);

        await using var session = OpenSession();
        var all = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        var cutoff = all[1].Timestamp + (all[2].Timestamp - all[1].Timestamp) / 2;

        var trail = await EventsFor(session)
            .AggregateStreamAsync<ComplianceTrail>(streamId, timestamp: cutoff, token: Cancellation);

        trail.ShouldNotBeNull();
        trail.Miles.ShouldBe(12);
    }

    [Fact]
    public async Task aggregate_stream_for_an_unknown_stream_is_null()
    {
        await using var session = OpenSession();
        var trail = await EventsFor(session)
            .AggregateStreamAsync<ComplianceTrail>(Guid.NewGuid(), token: Cancellation);

        trail.ShouldBeNull();
    }

    [Fact]
    public async Task aggregate_stream_is_null_once_the_fold_deletes_the_aggregate()
    {
        var streamId = await aTrailAsync(
            new TrailStarted("Pennine Way"),
            new MilesWalked(12),
            new TrailAbandoned());

        await using var session = OpenSession();
        var trail = await EventsFor(session).AggregateStreamAsync<ComplianceTrail>(streamId, token: Cancellation);

        trail.ShouldBeNull();
    }

    [Fact]
    public async Task aggregate_stream_to_last_known_walks_back_past_the_deletion()
    {
        var streamId = await aTrailAsync(
            new TrailStarted("Pennine Way"),
            new MilesWalked(12),
            new TrailAbandoned());

        await using var session = OpenSession();
        var trail = await EventsFor(session)
            .AggregateStreamToLastKnownAsync<ComplianceTrail>(streamId, token: Cancellation);

        // The whole point of the method: the current fold is null, so the store hands back the last
        // version that was not.
        trail.ShouldNotBeNull();
        trail.Name.ShouldBe("Pennine Way");
        trail.Miles.ShouldBe(12);
    }

    [Fact]
    public async Task aggregate_stream_to_last_known_matches_aggregate_stream_when_nothing_is_deleted()
    {
        var streamId = await aTrailAsync(new TrailStarted("Pennine Way"), new MilesWalked(12));

        await using var session = OpenSession();
        var lastKnown = await EventsFor(session)
            .AggregateStreamToLastKnownAsync<ComplianceTrail>(streamId, token: Cancellation);
        var plain = await EventsFor(session)
            .AggregateStreamAsync<ComplianceTrail>(streamId, token: Cancellation);

        lastKnown.ShouldNotBeNull();
        plain.ShouldNotBeNull();
        lastKnown.Miles.ShouldBe(plain.Miles);
    }
}
