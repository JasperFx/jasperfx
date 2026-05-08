using System.Text.Json;
using JasperFx.Descriptors;
using Shouldly;

namespace EventTests.Descriptors;

public class ExplorerDescriptorTests
{
    [Fact]
    public void stream_summary_round_trips_json()
    {
        var summary = new StreamSummary(
            "stream-1", "Order", 12,
            new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 8, 12, 30, 0, TimeSpan.Zero),
            "tenant-a");

        var json = JsonSerializer.Serialize(summary);
        var roundTripped = JsonSerializer.Deserialize<StreamSummary>(json);

        roundTripped.ShouldBe(summary);
    }

    [Fact]
    public void stream_metadata_round_trips_json()
    {
        var metadata = new StreamMetadata(
            "stream-1", "Order", 100,
            new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 8, 12, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 7, 9, 0, 0, TimeSpan.Zero),
            90, false, "tenant-a",
            new Dictionary<string, string> { ["customer"] = "C-42" });

        var json = JsonSerializer.Serialize(metadata);
        var roundTripped = JsonSerializer.Deserialize<StreamMetadata>(json)!;

        roundTripped.StreamId.ShouldBe(metadata.StreamId);
        roundTripped.Version.ShouldBe(metadata.Version);
        roundTripped.LastSnapshotVersion.ShouldBe(90);
        roundTripped.Tags["customer"].ShouldBe("C-42");
    }

    [Fact]
    public void event_record_round_trips_json()
    {
        var dataDoc = JsonDocument.Parse("""{"name":"Acme"}""");
        var record = new EventRecord(
            Guid.NewGuid(), 42, 3, "stream-1",
            "OrderPlaced", dataDoc.RootElement, null,
            DateTimeOffset.UtcNow, "tenant-a",
            new Dictionary<string, string> { ["customer"] = "C-42" });

        var json = JsonSerializer.Serialize(record);
        var roundTripped = JsonSerializer.Deserialize<EventRecord>(json)!;

        roundTripped.EventId.ShouldBe(record.EventId);
        roundTripped.Sequence.ShouldBe(42);
        roundTripped.EventTypeName.ShouldBe("OrderPlaced");
        roundTripped.Tags!["customer"].ShouldBe("C-42");
    }

    [Fact]
    public void dcb_tag_descriptor_round_trips_json()
    {
        var tag = new DcbTagDescriptor(
            "CustomerId", "System.String",
            TypeDescriptor.For(typeof(string)),
            "Identifies the customer");

        var json = JsonSerializer.Serialize(tag);
        var roundTripped = JsonSerializer.Deserialize<DcbTagDescriptor>(json);

        roundTripped.ShouldBe(tag);
    }

    [Fact]
    public void aggregate_at_version_round_trips_json()
    {
        var raw = new AggregateAtVersion(
            "MyApp.Order",
            JsonDocument.Parse("""{"total":99}""").RootElement,
            5, 5);

        var json = JsonSerializer.Serialize(raw);
        var roundTripped = JsonSerializer.Deserialize<AggregateAtVersion>(json)!;

        roundTripped.TypeName.ShouldBe("MyApp.Order");
        roundTripped.Version.ShouldBe(5);
        roundTripped.EventsApplied.ShouldBe(5);
    }

    [Fact]
    public void dcb_projected_state_round_trips_json()
    {
        var state = new DcbProjectedState(
            "BalanceProjection",
            17,
            JsonDocument.Parse("""{"balance":100}""").RootElement,
            17);

        var json = JsonSerializer.Serialize(state);
        var roundTripped = JsonSerializer.Deserialize<DcbProjectedState>(json)!;

        roundTripped.ProjectionName.ShouldBe("BalanceProjection");
        roundTripped.Version.ShouldBe(17);
        roundTripped.EventsApplied.ShouldBe(17);
    }

    [Fact]
    public void projection_status_round_trips_json()
    {
        var status = new ProjectionStatus("Trip", "Async",
            new[]
            {
                new ShardStatus("Trip:All", "Running", 100, 200, null),
                new ShardStatus("Trip:Failed", "Failed", 50, 200, "boom"),
            });

        var json = JsonSerializer.Serialize(status);
        var roundTripped = JsonSerializer.Deserialize<ProjectionStatus>(json)!;

        roundTripped.ProjectionName.ShouldBe("Trip");
        roundTripped.Lifecycle.ShouldBe("Async");
        roundTripped.Shards.Count.ShouldBe(2);
        roundTripped.Shards[1].Error.ShouldBe("boom");
    }

    [Fact]
    public void event_type_descriptor_round_trips_json()
    {
        var descriptor = new EventTypeDescriptor(
            TypeDescriptor.For(typeof(string)), "string", "the string event");

        var json = JsonSerializer.Serialize(descriptor);
        var roundTripped = JsonSerializer.Deserialize<EventTypeDescriptor>(json);

        roundTripped.ShouldBe(descriptor);
    }

    [Fact]
    public void saga_type_descriptor_round_trips_json()
    {
        var descriptor = new SagaTypeDescriptor(
            TypeDescriptor.For(typeof(string)),
            new[] { TypeDescriptor.For(typeof(int)) },
            new[] { TypeDescriptor.For(typeof(long)) },
            "Marten");

        var json = JsonSerializer.Serialize(descriptor);
        var roundTripped = JsonSerializer.Deserialize<SagaTypeDescriptor>(json)!;

        roundTripped.SagaType.ShouldBe(descriptor.SagaType);
        roundTripped.StartingMessages.Count.ShouldBe(1);
        roundTripped.StorageProvider.ShouldBe("Marten");
    }

    [Fact]
    public void projection_timeline_raw_round_trips_json()
    {
        var ev = new EventRecord(
            Guid.NewGuid(), 1, 1, "stream-1",
            "X", JsonDocument.Parse("{}").RootElement, null,
            DateTimeOffset.UtcNow, null, null);

        var step = new ProjectionStepResultRaw(
            ev,
            JsonDocument.Parse("""{"v":0}""").RootElement,
            JsonDocument.Parse("""{"v":1}""").RootElement,
            TimeSpan.FromMilliseconds(3),
            null);

        var timeline = new ProjectionTimelineRaw(new[] { step },
            JsonDocument.Parse("""{"v":1}""").RootElement);

        var json = JsonSerializer.Serialize(timeline);
        var roundTripped = JsonSerializer.Deserialize<ProjectionTimelineRaw>(json)!;

        roundTripped.Steps.Count.ShouldBe(1);
        roundTripped.Steps[0].Event.EventTypeName.ShouldBe("X");
        roundTripped.FinalState.HasValue.ShouldBeTrue();
    }

    [Fact]
    public void multi_aggregate_projection_result_round_trips_json()
    {
        var ev = new EventRecord(
            Guid.NewGuid(), 1, 1, "stream-1",
            "X", JsonDocument.Parse("{}").RootElement, null,
            DateTimeOffset.UtcNow, null, null);
        var step = new ProjectionStepResultRaw(ev, null, null, TimeSpan.Zero, null);
        var timeline = new ProjectionTimelineRaw(new[] { step }, null);
        var result = new MultiAggregateProjectionResult(
            "Sliced",
            new Dictionary<string, ProjectionTimelineRaw> { ["agg-1"] = timeline });

        var json = JsonSerializer.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<MultiAggregateProjectionResult>(json)!;

        roundTripped.ProjectionName.ShouldBe("Sliced");
        roundTripped.AggregatesByIdentity.Count.ShouldBe(1);
        roundTripped.AggregatesByIdentity["agg-1"].Steps.Count.ShouldBe(1);
    }
}
