using System.Text.Json;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests;

// jasperfx#407 Phase 0: the new nullable TenantId slot on ShardState / HighWaterStatistics must be
// purely additive -- default to null and never break existing serialized payloads that predate it.
public class TenantIdSurfaceSerializationTests
{
    [Fact]
    public void shard_state_defaults_tenant_to_null()
    {
        new ShardState("Foo:All", 5).TenantId.ShouldBeNull();
    }

    [Fact]
    public void shard_state_copies_tenant_from_shard_name()
    {
        var state = new ShardState(ShardName.Compose("Foo", "All", "tenant1"), 5);
        state.TenantId.ShouldBe("tenant1");
    }

    [Fact]
    public void shard_state_serializes_null_tenant_without_throwing()
    {
        // ShardState is published in-memory rather than JSON round-tripped, but writing it must
        // still emit the additive slot as null so any consumer that does serialize it sees null.
        var json = JsonSerializer.Serialize(new ShardState("Foo:All", 5));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("TenantId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void shard_state_serializes_set_tenant()
    {
        var json = JsonSerializer.Serialize(new ShardState("Foo:All", 5) { TenantId = "tenant1" });
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("TenantId").GetString().ShouldBe("tenant1");
    }

    [Fact]
    public void high_water_statistics_defaults_tenant_to_null()
    {
        new HighWaterStatistics().TenantId.ShouldBeNull();
    }

    [Fact]
    public void high_water_statistics_round_trips_tenant()
    {
        var stats = new HighWaterStatistics { CurrentMark = 10, TenantId = "tenant1" };
        var restored = JsonSerializer.Deserialize<HighWaterStatistics>(JsonSerializer.Serialize(stats));
        restored!.TenantId.ShouldBe("tenant1");
        restored.CurrentMark.ShouldBe(10);
    }

    [Fact]
    public void existing_high_water_payload_without_tenant_still_deserializes()
    {
        const string legacy = """{"LastMark":3,"CurrentMark":7,"HighestSequence":7}""";
        var restored = JsonSerializer.Deserialize<HighWaterStatistics>(legacy);
        restored!.TenantId.ShouldBeNull();
        restored.CurrentMark.ShouldBe(7);
    }
}
