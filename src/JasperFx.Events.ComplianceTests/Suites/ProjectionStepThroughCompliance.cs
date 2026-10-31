using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The projection step-through on <see cref="IEventStore"/>: <c>RunProjectionAsync</c> and
/// <c>RunProjectionByNameAsync</c> over rows the store really wrote (jasperfx#1049).
/// </summary>
/// <remarks>
/// <para>
/// The fold and the bookkeeping around it are JasperFx's and unit tested there. What only a store can
/// get wrong is its half of the seam: that the serializer it hands
/// <see cref="ProjectionStateSerializer"/> carries a real aggregate both ways, and that its
/// <c>toEvent</c> reads back what its own explorer reads produced. Polecat's replay predated the shared
/// fold and recorded live references, so every step showed the final state; that is the first fact here.
/// </para>
/// <para>
/// A suite of its own rather than facts on <c>EventStoreExplorerCompliance</c>, so a store enrolls it
/// when it adopts <see cref="ProjectionStepThrough{TQuerySession}"/> rather than going red on the next
/// JasperFx bump.
/// </para>
/// </remarks>
public abstract class ProjectionStepThroughCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_step_through";
        config.AddEventType<VoyageBegun>();
        config.AddEventType<PortVisited>();
        config.Snapshot<VoyageSnapshot>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<(Guid StreamId, List<EventRecord> Events)> aVoyageAsync()
    {
        if (!theFixture.SupportsExplorerSurface)
        {
            Assert.Skip("This store does not implement the event store explorer surface.");
        }

        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new VoyageBegun("Beagle"), new PortVisited("Plymouth"),
            new PortVisited("Bahia"));
        await SaveChangesAsync(session);

        var events = new List<EventRecord>();
        await foreach (var record in EventStore.ReadStreamAsync(streamId.ToString(), Cancellation))
        {
            events.Add(record);
        }

        return (streamId, events);
    }

    private static int portsIn(JsonElement? state)
        => state!.Value.EnumerateObject().Single(x => x.Name.Equals("ports", StringComparison.OrdinalIgnoreCase))
            .Value.GetArrayLength();

    [Fact]
    public async Task each_typed_step_holds_the_state_as_of_that_step()
    {
        var (streamId, events) = await aVoyageAsync();

        var timeline = await EventStore.RunProjectionAsync<VoyageSnapshot>(nameof(VoyageSnapshot), streamId, events,
            null, Cancellation);

        timeline.Steps.Count.ShouldBe(3);
        timeline.Steps[0].Before.ShouldBeNull();
        timeline.Steps.Select(x => x.After!.Ports.Count).ShouldBe([0, 1, 2]);
        timeline.Steps.ShouldAllBe(x => x.Error == null);
        timeline.FinalState!.Ports.ShouldBe(["Plymouth", "Bahia"]);
    }

    [Fact]
    public async Task each_step_by_name_holds_the_state_as_of_that_step()
    {
        var (streamId, events) = await aVoyageAsync();

        var timeline = await EventStore.RunProjectionByNameAsync(nameof(VoyageSnapshot), streamId, events, null,
            Cancellation);

        timeline.Steps.Select(x => portsIn(x.After)).ShouldBe([0, 1, 2]);
        portsIn(timeline.FinalState).ShouldBe(2);
    }

    [Fact]
    public async Task a_starting_state_is_left_as_it_was()
    {
        var (streamId, events) = await aVoyageAsync();

        var start = new VoyageSnapshot { Id = streamId, Ship = "Endeavour", Ports = ["Madeira"] };

        var timeline = await EventStore.RunProjectionAsync(nameof(VoyageSnapshot), streamId, events.Skip(1).ToList(),
            start, Cancellation);

        timeline.FinalState!.Ports.ShouldBe(["Madeira", "Plymouth", "Bahia"]);
        start.Ports.ShouldBe(["Madeira"]);
    }

    [Fact]
    public async Task an_event_type_the_store_does_not_know_changes_nothing()
    {
        var (streamId, events) = await aVoyageAsync();

        events.Insert(1, events[1] with { EventTypeName = "from_a_newer_deployment" });

        var timeline = await EventStore.RunProjectionAsync<VoyageSnapshot>(nameof(VoyageSnapshot), streamId, events,
            null, Cancellation);

        timeline.Steps.Count.ShouldBe(4);
        timeline.Steps[1].Error.ShouldBeNull();
        timeline.Steps[1].After!.Ports.ShouldBeEmpty();
        timeline.FinalState!.Ports.Count.ShouldBe(2);
    }
}
