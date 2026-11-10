using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.OpenTelemetry;
using NSubstitute;
using Shouldly;

namespace EventTests.OpenTelemetry;

/// <summary>
/// <see cref="SubscriptionMetrics" /> reaches the instruments through <see cref="IEventStore.Meter" /> and
/// <see cref="IEventStore.ActivitySource" />, and a store answering those from its options is what removes
/// the second Meter and ActivitySource jasperfx#1054 is about. These hold the daemon's half of that.
/// </summary>
public class SubscriptionMetricsOverOptionsTests
{
    [Fact]
    public void the_daemons_spans_and_measurements_arrive_once_on_the_options_instruments()
    {
        var name = $"jasperfx.tests.otel.{Guid.NewGuid():N}";
        var options = new OpenTelemetryOptions(name);

        var store = Substitute.For<IEventStore>();
        store.Meter.Returns(options.Meter);
        store.ActivitySource.Returns(options.ActivitySource);
        store.MetricsPrefix.Returns("fake");

        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        var processed = new List<long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == name) listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => processed.Add(value));
        meterListener.Start();

        var shard = new ShardName("Trips", "All", 1);
        var metrics = new SubscriptionMetrics(store, shard, new Uri("database://main"));

        metrics.TrackExecution(new EventRange(shard, 10) { Events = [] })!.Dispose();
        metrics.UpdateProcessed(7);

        spans.Single().OperationName.ShouldBe("fake.trips.all.page.execution");
        spans.Single().Source.ShouldBeSameAs(options.ActivitySource);
        processed.ShouldBe([7L]);
    }
}
