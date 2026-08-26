using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Explorer events

public record VoyageBegun(string Ship);

public record PortVisited(string Port);

#endregion

/// <summary>
/// An aggregate registered as an <see cref="ProjectionLifecycle.Inline"/> snapshot, so the suite has
/// a projection registration to look for in <c>EventStoreUsage.Subscriptions</c>.
/// </summary>
/// <remarks>
/// Inline rather than async on purpose: an implementation that described the daemon's shards instead
/// of the registrations would look correct and still answer nothing for an inline-only store, which
/// is exactly the shape of the store that went several releases describing none of its projections
/// (JasperFx/fisher#120). <c>SubscriptionDescriptor</c> already handles Inline — its metrics block is
/// gated on <see cref="ProjectionLifecycle.Async"/> — so requiring it costs nothing.
/// </remarks>
public partial class VoyageSnapshot
{
    public Guid Id { get; set; }
    public string Ship { get; set; } = string.Empty;
    public List<string> Ports { get; set; } = new();

    public static VoyageSnapshot Create(VoyageBegun e) => new() { Ship = e.Ship };

    public void Apply(PortVisited e) => Ports.Add(e.Port);
}

/// <summary>
/// The event store explorer surface on <see cref="IEventStore"/> — <c>GetRecentStreamsAsync</c>,
/// <c>GetStreamMetadataAsync</c> and <c>TryCreateUsage</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are default-interface methods that throw unless a store implements them, which makes them
/// unusually easy to leave half-built: nothing fails at compile time, and each product has its own
/// tests. They also have an out-of-repo consumer — this is the surface CritterWatch reads — so a
/// cross-store contract test is the cheapest guard against a tooling regression on one store only.
/// </para>
/// <para>
/// Per-tenant overloads are deliberately not exercised here; they belong with the conjoined tenancy
/// suite, which owns the tenant-scoped session seam.
/// </para>
/// </remarks>
public abstract class EventStoreExplorerCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_explorer";
        config.AddEventType<VoyageBegun>();
        config.AddEventType<PortVisited>();
        config.Snapshot<VoyageSnapshot>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private bool SupportsExplorer => theFixture.SupportsExplorerSurface;

    private async Task<Guid> aVoyageAsync(params object[] extra)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = new object[] { new VoyageBegun("Beagle") }.Concat(extra).ToArray();
        EventsFor(session).StartStream(streamId, events);
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task recent_streams_reports_the_streams_that_exist()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var first = await aVoyageAsync();
        var second = await aVoyageAsync(new PortVisited("Plymouth"));

        var streams = await EventStore.GetRecentStreamsAsync(10, Cancellation);

        var ids = streams.Select(x => x.StreamId).ToList();
        ids.ShouldContain(first.ToString());
        ids.ShouldContain(second.ToString());

        var summary = streams.Single(x => x.StreamId == second.ToString());
        summary.Version.ShouldBe(2);
        summary.CreatedAt.ShouldNotBe(default);
        summary.LastUpdatedAt.ShouldNotBe(default);
        summary.LastUpdatedAt.ShouldBeGreaterThanOrEqualTo(summary.CreatedAt);
    }

    [Fact]
    public async Task recent_streams_honours_the_count_and_orders_newest_first()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        await aVoyageAsync();
        await Task.Delay(20, Cancellation);
        await aVoyageAsync();
        await Task.Delay(20, Cancellation);
        var newest = await aVoyageAsync();

        var streams = await EventStore.GetRecentStreamsAsync(2, Cancellation);

        streams.Count.ShouldBe(2);
        streams[0].StreamId.ShouldBe(newest.ToString());
        streams[0].LastUpdatedAt.ShouldBeGreaterThanOrEqualTo(streams[1].LastUpdatedAt);
    }

    [Fact]
    public async Task asking_for_more_streams_than_exist_is_not_an_error()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        await aVoyageAsync();

        var streams = await EventStore.GetRecentStreamsAsync(1000, Cancellation);

        streams.Count.ShouldBeGreaterThanOrEqualTo(1);
        streams.Count.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task stream_metadata_for_a_known_stream()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var streamId = await aVoyageAsync(new PortVisited("Plymouth"), new PortVisited("Bahia"));

        var metadata = await EventStore.GetStreamMetadataAsync(streamId.ToString(), Cancellation);

        metadata.ShouldNotBeNull();
        metadata.StreamId.ShouldBe(streamId.ToString());
        metadata.Version.ShouldBe(3);
        metadata.IsArchived.ShouldBeFalse();
        metadata.CreatedAt.ShouldNotBe(default);

        // Non-nullable in the record's declaration: "no tags" is an empty dictionary, not null.
        // Polecat returned null here until polecat#412; this assertion is what found it.
        metadata.Tags.ShouldNotBeNull();
    }

    [Fact]
    public async Task stream_metadata_for_an_unknown_stream_is_null()
    {
        if (!SupportsExplorer)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var metadata = await EventStore.GetStreamMetadataAsync(Guid.NewGuid().ToString(), Cancellation);

        metadata.ShouldBeNull();
    }

    [Fact]
    public async Task usage_describes_the_registered_event_types()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        // TryCreateUsage is allowed to return null for a store that cannot describe itself, but a
        // store that returns one must know the event types it was configured with.
        if (usage == null)
        {
            return;
        }

        var names = usage.Events.Select(x => x.EventTypeName).ToList();
        names.ShouldContain(EventTypeNameFor<VoyageBegun>());
        names.ShouldContain(EventTypeNameFor<PortVisited>());
    }

    [Fact]
    public async Task usage_describes_the_registered_projections()
    {
        var usage = await EventStore.TryCreateUsage(Cancellation);

        // Same escape as above: a store that cannot describe itself is a legitimate answer. What is
        // being caught here is a store that DOES return a usage and silently under-fills it.
        if (usage == null)
        {
            return;
        }

        // Every member of EventStoreUsage is a list or a nullable that starts empty, so an unfilled
        // slot is indistinguishable from a genuinely empty one — no exception, no warning, and no
        // field saying which of the two it is. Consumers then render a confident wrong answer:
        // "projections list" prints "No projections in this store" for a store with twenty of them,
        // "projections rebuild" matches none of them, and CritterWatch sees a store with no
        // projections. That is JasperFx/fisher#120, where the fix was one missing Describe() call.
        usage.Subscriptions.ShouldNotBeEmpty(
            "The store returned a usage descriptor but left Subscriptions empty, even though a projection was registered. Two shipped commands read this slot directly.");

        usage.Subscriptions.Select(x => x.Name).ShouldContain(nameof(VoyageSnapshot));
    }
}
