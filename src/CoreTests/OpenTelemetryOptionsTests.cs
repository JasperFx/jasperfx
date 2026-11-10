using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.OpenTelemetry;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void the_source_is_named_like_the_meter_and_versioned_by_the_concrete_options_type()
    {
        var options = new FakeStoreOtelOptions();

        options.ActivitySource.Name.ShouldBe(options.Meter.Name);
        options.ActivitySource.Version.ShouldBe(typeof(FakeStoreOtelOptions).Assembly.GetName().Version!.ToString());
    }

    // jasperfx#1054. Polecat and Fisher each held a second Meter and ActivitySource of the same name on top of
    // this base, and a listener subscribed by name heard both. One per store would not be the end of it
    // either: an ancillary store has its own options, so these have to be shared by name.
    [Fact]
    public void one_name_is_one_source_and_one_meter_however_many_stores_ask()
    {
        var name = uniqueName();
        var sources = new List<ActivitySource>();
        using var listener = listenTo(name, sources);

        var first = new OpenTelemetryOptions(name);
        var second = new OpenTelemetryOptions(name);

        second.ActivitySource.ShouldBeSameAs(first.ActivitySource);
        second.Meter.ShouldBeSameAs(first.Meter);
        sources.Count.ShouldBe(1);
    }

    [Fact]
    public void racing_for_a_name_still_builds_one_source_and_one_meter()
    {
        var name = uniqueName();
        var sources = new List<ActivitySource>();
        using var listener = listenTo(name, sources);

        var options = Enumerable.Range(0, 64).AsParallel().Select(_ =>
        {
            var o = new OpenTelemetryOptions(name);
            return (o.Meter, o.ActivitySource);
        }).ToArray();

        options.Select(x => x.Meter).Distinct().Count().ShouldBe(1);
        options.Select(x => x.ActivitySource).Distinct().Count().ShouldBe(1);
        sources.Count.ShouldBe(1);
    }

    // A store still on its own tracing holder must not gain a second source just by constructing its options,
    // or shipping this before the stores adopt it would give Marten the bug it does not have today.
    [Fact]
    public void constructing_options_creates_no_source_until_something_asks_for_one()
    {
        var name = uniqueName();
        var sources = new List<ActivitySource>();
        using var listener = listenTo(name, sources);

        var options = new OpenTelemetryOptions(name);
        sources.ShouldBeEmpty();

        _ = options.ActivitySource;
        sources.Count.ShouldBe(1);
    }

    [Fact]
    public void a_span_is_heard_once_by_a_listener_subscribed_by_name()
    {
        var name = uniqueName();
        var stopped = new List<Activity>();
        using var listener = listenTo(name, new List<ActivitySource>(), stopped);

        new OpenTelemetryOptions(name).StartActivity("work")!.Dispose();
        new OpenTelemetryOptions(name).StartActivity("more")!.Dispose();

        stopped.Select(x => x.OperationName).ShouldBe(["work", "more"]);
        stopped.ShouldAllBe(x => x.Source.Name == name);
    }

    [Fact]
    public void no_listener_means_no_span()
    {
        new OpenTelemetryOptions(uniqueName()).StartActivity("work").ShouldBeNull();
    }

    // marten#5459: `parentActivity?.ParentId` re-parented every span to its grandparent whenever the ambient
    // activity had a parent, which is the shape of an incoming request carrying a remote traceparent.
    [Fact]
    public void a_span_parents_to_the_activity_it_was_handed_and_not_to_that_activitys_parent()
    {
        var name = uniqueName();
        using var listener = listenTo(name, new List<ActivitySource>());

        using var incoming = new Activity("incoming")
            .SetParentId("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")
            .Start();

        using var span = new OpenTelemetryOptions(name).StartActivity("work", incoming)!;

        span.ParentSpanId.ShouldBe(incoming.SpanId);
        span.TraceId.ShouldBe(incoming.TraceId);
    }

    [Fact]
    public void a_span_with_no_parent_handed_to_it_takes_the_ambient_activity()
    {
        var name = uniqueName();
        using var listener = listenTo(name, new List<ActivitySource>());

        using var ambient = new Activity("ambient").Start();

        using var span = new OpenTelemetryOptions(name).StartActivity("work")!;

        span.ParentSpanId.ShouldBe(ambient.SpanId);
    }

    [Fact]
    public void a_span_carries_its_tags_and_kind()
    {
        var name = uniqueName();
        using var listener = listenTo(name, new List<ActivitySource>());
        var tags = new ActivityTagsCollection { { "tenant.id", "green" } };

        using var span = new OpenTelemetryOptions(name)
            .StartActivity("work", tags: tags, activityKind: ActivityKind.Client)!;

        span.Kind.ShouldBe(ActivityKind.Client);
        span.GetTagItem("tenant.id").ShouldBe("green");
    }

    // OptionsDescription reflects over public properties, so anything not marked here turns up in CritterWatch's
    // view of the store: a Meter's worth of nothing, and a list of delegates.
    [Fact]
    public void the_instruments_stay_out_of_the_options_description()
    {
        var options = new OpenTelemetryOptions<FakeChangeSet>(uniqueName());
        options.ExportCounterOnChangeSets<long>(uniqueName(), "things", (_, _) => { });

        OptionsDescription.For(options).Properties.Select(x => x.Name).ShouldBe([nameof(options.TrackConnections)]);
    }

    [Fact]
    public void no_counters_means_nothing_to_apply()
    {
        var options = new OpenTelemetryOptions<FakeChangeSet>(uniqueName());

        options.Applications.ShouldBeEmpty();
        options.RecordCommit(new FakeChangeSet(1));
    }

    [Fact]
    public void an_exported_counter_is_applied_to_every_commit()
    {
        var options = new OpenTelemetryOptions<FakeChangeSet>(uniqueName());
        var counterName = uniqueName();
        using var measured = new Measurements(options.Meter, counterName);

        options.ExportCounterOnChangeSets<long>(counterName, "things", (counter, commit) => counter.Add(commit.Things));

        options.Applications.Count.ShouldBe(1);

        options.RecordCommit(new FakeChangeSet(2));
        options.RecordCommit(new FakeChangeSet(3));

        measured.Values.ShouldBe([2L, 3L]);
    }

    [Fact]
    public void a_counter_two_stores_export_under_one_name_reports_each_add_once()
    {
        var name = uniqueName();
        var counterName = uniqueName();
        var first = new OpenTelemetryOptions<FakeChangeSet>(name);
        var second = new OpenTelemetryOptions<FakeChangeSet>(name);
        using var measured = new Measurements(first.Meter, counterName);

        first.ExportCounterOnChangeSets<long>(counterName, "things", (counter, commit) => counter.Add(commit.Things));
        second.ExportCounterOnChangeSets<long>(counterName, "things", (counter, commit) => counter.Add(commit.Things));

        first.RecordCommit(new FakeChangeSet(2));
        second.RecordCommit(new FakeChangeSet(3));

        measured.Values.ShouldBe([2L, 3L]);
    }

    // The write is durable by the time this runs, so throwing would tell the caller a committed write failed.
    [Fact]
    public void a_failing_counter_is_logged_and_does_not_stop_the_next_one()
    {
        var options = new OpenTelemetryOptions<FakeChangeSet>(uniqueName());
        var counterName = uniqueName();
        using var measured = new Measurements(options.Meter, counterName);
        var logger = new RecordingLogger();

        options.ExportCounterOnChangeSets<long>(uniqueName(), "things", (_, _) => throw new DivideByZeroException());
        options.ExportCounterOnChangeSets<long>(counterName, "things", (counter, commit) => counter.Add(commit.Things));

        options.RecordCommit(new FakeChangeSet(4), logger);

        measured.Values.ShouldBe([4L]);
        logger.Errors.Single().ShouldBeOfType<DivideByZeroException>();
    }

    [Fact]
    public void a_failing_counter_with_no_logger_still_does_not_throw()
    {
        var options = new OpenTelemetryOptions<FakeChangeSet>(uniqueName());
        options.ExportCounterOnChangeSets<long>(uniqueName(), "things", (_, _) => throw new DivideByZeroException());

        Should.NotThrow(() => options.RecordCommit(new FakeChangeSet(1)));
    }

    [Fact]
    public void a_null_record_action_is_refused_when_exporting_and_not_at_the_first_commit()
    {
        var options = new OpenTelemetryOptions<FakeChangeSet>(uniqueName());

        Should.Throw<ArgumentNullException>(() =>
            options.ExportCounterOnChangeSets<long>(uniqueName(), "things", null!));
    }

    private static string uniqueName() => $"jasperfx.tests.otel.{Guid.NewGuid():N}";

    private static ActivityListener listenTo(string name, List<ActivitySource> sources, List<Activity>? stopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
            {
                if (source.Name != name) return false;

                lock (sources) sources.Add(source);
                return true;
            },
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped?.Add(activity)
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FakeStoreOtelOptions() : OpenTelemetryOptions("FakeStore")
    {
        // Stand-in for Marten's ExportCounterOnChangeSets, which uses the inherited Meter.
        public Counter<long> AddCounter(string name, string units) => Meter.CreateCounter<long>(name, units);
    }

    private sealed record FakeChangeSet(long Things);

    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Measurements(Meter meter, string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == meter && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Values.Add(value));
            _listener.Start();
        }

        public List<long> Values { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception);
        }
    }
}
