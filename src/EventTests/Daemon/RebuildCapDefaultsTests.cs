using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Shouldly;

namespace EventTests.Daemon;

// jasperfx#420 / epic #486 WS3: rebuild-cap configuration surface.
public class RebuildCapDefaultsTests
{
    [Fact]
    public void daemon_settings_default_is_null_meaning_store_derived()
    {
        // Null = "derive store-side" (Marten/Polecat fall back to pool-size / 8);
        // JasperFx.Events itself treats null as unbounded because it has no pool signal.
        new DaemonSettings().MaxConcurrentRebuildsPerDatabase.ShouldBeNull();
    }

    [Fact]
    public void event_store_usage_field_defaults_to_null_and_round_trips()
    {
        // jasperfx#434: monitoring tools (CritterWatch#309) read the effective cap off
        // the descriptor; null means "not populated" and consumers fall back to 1.
        var usage = new EventStoreUsage();
        usage.MaxConcurrentRebuildsPerDatabase.ShouldBeNull();

        usage.MaxConcurrentRebuildsPerDatabase = 12;
        usage.MaxConcurrentRebuildsPerDatabase.ShouldBe(12);
    }
}
