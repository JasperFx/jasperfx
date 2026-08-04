using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Explorer events

public record VoyageBegun(string Ship);

public record PortVisited(string Port);

#endregion

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
}
