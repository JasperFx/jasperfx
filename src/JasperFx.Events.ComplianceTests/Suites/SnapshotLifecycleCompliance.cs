using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Snapshot lifecycle events and aggregate

public record ParcelShipped(string Destination);

public record ParcelScanned(string Location);

public record ParcelDelivered;

/// <summary>
/// One aggregate, folded the same way whichever lifecycle persists it.
/// </summary>
public partial class ParcelSnapshot
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;
    public List<string> Scans { get; set; } = new();
    public bool Delivered { get; set; }

    public static ParcelSnapshot Create(ParcelShipped e) => new() { Destination = e.Destination };

    public void Apply(ParcelScanned e) => Scans.Add(e.Location);

    public void Apply(ParcelDelivered e) => Delivered = true;
}

#endregion

/// <summary>
/// Equivalence across <see cref="SnapshotLifecycle"/> — the same aggregate registered
/// <c>Inline</c> or <c>Async</c>, or folded live, must produce the same state. Only *when* the state
/// becomes visible is allowed to differ.
/// </summary>
/// <remarks>
/// <para>
/// The individual lifecycles are already exercised elsewhere: <c>SelfAggregatingEvolveCompliance</c>
/// and <c>FetchForWritingCompliance</c> use inline snapshots, <c>AsyncDaemonCompliance</c> drives an
/// async one, <c>LiveAggregationCompliance</c> folds without registration. What none of them assert
/// is that the three agree, which is the property users actually rely on when they move a projection
/// from inline to async to keep writes cheap.
/// </para>
/// <para>
/// The timing half is asserted as deliberately as the equivalence half. An inline snapshot is
/// readable the moment its transaction commits; an async one is not readable until the daemon has
/// caught up. That difference is the entire reason to choose between them, so a store that quietly
/// persisted an async projection inline would be wrong in a way no equivalence assertion catches.
/// </para>
/// </remarks>
public abstract class SnapshotLifecycleCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_snapshot_lifecycle";

        config.AddEventType<ParcelShipped>();
        config.AddEventType<ParcelScanned>();
        config.AddEventType<ParcelDelivered>();

        config.Snapshot<ParcelSnapshot>(SnapshotLifecycle.Inline);
    };

    /// <summary>
    /// The same aggregate, same schema, registered async instead.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _asyncConfiguration = config =>
    {
        config.SchemaName = "compliance_snapshot_lifecycle";

        config.AddEventType<ParcelShipped>();
        config.AddEventType<ParcelScanned>();
        config.AddEventType<ParcelDelivered>();

        config.Snapshot<ParcelSnapshot>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static readonly object[] _story =
    [
        new ParcelShipped("Rivendell"),
        new ParcelScanned("Bree"),
        new ParcelScanned("Weathertop"),
        new ParcelDelivered()
    ];

    private async Task<Guid> aParcelAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ParcelSnapshot>(streamId, events.Length == 0 ? _story : events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private static void shouldMatchTheStory(ParcelSnapshot? snapshot, Guid streamId)
    {
        snapshot.ShouldNotBeNull();
        snapshot.Id.ShouldBe(streamId);
        snapshot.Destination.ShouldBe("Rivendell");
        snapshot.Scans.ToArray().ShouldBe(new[] { "Bree", "Weathertop" });
        snapshot.Delivered.ShouldBeTrue();
    }

    [Fact]
    public async Task an_inline_snapshot_matches_the_live_fold()
    {
        var streamId = await aParcelAsync();

        await using var query = OpenSession();

        var persisted = await LoadDocumentAsync<ParcelSnapshot>(query, streamId);
        shouldMatchTheStory(persisted, streamId);

        // Same events, folded on demand rather than read back.
        var live = await EventsFor(query).AggregateStreamAsync<ParcelSnapshot>(streamId, token: Cancellation);
        shouldMatchTheStory(live, streamId);

        persisted!.Scans.ToArray().ShouldBe(live!.Scans.ToArray());
        persisted.Delivered.ShouldBe(live.Delivered);
        persisted.Destination.ShouldBe(live.Destination);
    }

    [Fact]
    public async Task an_inline_snapshot_is_readable_as_soon_as_it_commits()
    {
        var streamId = await aParcelAsync();

        // No daemon, no waiting -- that is what inline means.
        await using var query = OpenSession();
        (await LoadDocumentAsync<ParcelSnapshot>(query, streamId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task an_async_snapshot_matches_the_live_fold_once_the_daemon_catches_up()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncConfiguration);

        var streamId = await aParcelAsync();

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();

        var persisted = await LoadDocumentAsync<ParcelSnapshot>(query, streamId);
        shouldMatchTheStory(persisted, streamId);

        var live = await EventsFor(query).AggregateStreamAsync<ParcelSnapshot>(streamId, token: Cancellation);
        shouldMatchTheStory(live, streamId);
    }

    [Fact]
    public async Task an_async_snapshot_is_not_written_by_the_committing_transaction()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncConfiguration);

        var streamId = await aParcelAsync();

        // The whole point of choosing async over inline: the write path does not pay for the
        // projection. No daemon has been started, so nothing should have been persisted yet.
        await using var query = OpenSession();
        (await LoadDocumentAsync<ParcelSnapshot>(query, streamId)).ShouldBeNull();
    }

    [Fact]
    public async Task both_lifecycles_reach_the_same_state_from_the_same_events()
    {
        // Inline first, on the suite's standard configuration.
        var inlineStream = await aParcelAsync();

        ParcelSnapshot inline;
        await using (var query = OpenSession())
        {
            inline = (await LoadDocumentAsync<ParcelSnapshot>(query, inlineStream))!;
        }

        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        // Then the identical story through the async lifecycle.
        await theFixture.ConfigureAsync(_asyncConfiguration);

        var asyncStream = await aParcelAsync();

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var asyncQuery = OpenSession();
        var async = (await LoadDocumentAsync<ParcelSnapshot>(asyncQuery, asyncStream))!;

        // Everything except the identity, which is the stream's.
        async.Destination.ShouldBe(inline.Destination);
        async.Scans.ToArray().ShouldBe(inline.Scans.ToArray());
        async.Delivered.ShouldBe(inline.Delivered);
    }

    [Fact]
    public async Task a_snapshot_keeps_up_with_events_appended_after_it_was_created()
    {
        var streamId = await aParcelAsync(new ParcelShipped("Rivendell"), new ParcelScanned("Bree"));

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new ParcelScanned("Weathertop"), new ParcelDelivered());
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        shouldMatchTheStory(await LoadDocumentAsync<ParcelSnapshot>(query, streamId), streamId);
    }
}
