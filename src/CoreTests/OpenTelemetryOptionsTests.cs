using System.Diagnostics.Metrics;
using JasperFx.OpenTelemetry;
using Shouldly;

namespace CoreTests;

public class OpenTelemetryOptionsTests
{
    [Fact]
    public void track_level_ordinals()
    {
        ((int)TrackLevel.None).ShouldBe(0);
        ((int)TrackLevel.Normal).ShouldBe(1);
        ((int)TrackLevel.Verbose).ShouldBe(2);
    }

    [Fact]
    public void base_defaults_track_connections_to_none_and_names_the_meter()
    {
        var options = new OpenTelemetryOptions("Polecat");

        options.TrackConnections.ShouldBe(TrackLevel.None);
        options.Meter.Name.ShouldBe("Polecat");
    }

    // Models Marten's posture: a store-specific subclass that names the meter and adds the
    // changeset-metrics surface while inheriting TrackConnections + Meter from the base.
    [Fact]
    public void store_specific_subclass_inherits_base_and_adds_metrics()
    {
        var options = new FakeStoreOtelOptions();

        options.ShouldBeAssignableTo<OpenTelemetryOptions>();
        options.Meter.Name.ShouldBe("FakeStore");
        options.TrackConnections = TrackLevel.Verbose;
        options.TrackConnections.ShouldBe(TrackLevel.Verbose);

        var counter = options.AddCounter("things", "count");
        counter.ShouldNotBeNull();
    }

    private sealed class FakeStoreOtelOptions() : OpenTelemetryOptions("FakeStore")
    {
        // Stand-in for Marten's ExportCounterOnChangeSets, which uses the inherited Meter.
        public Counter<long> AddCounter(string name, string units) => Meter.CreateCounter<long>(name, units);
    }
}
