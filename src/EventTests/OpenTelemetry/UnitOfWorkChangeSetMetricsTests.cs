using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.OpenTelemetry;
using NSubstitute;
using Shouldly;

namespace EventTests.OpenTelemetry;

public class UnitOfWorkChangeSetMetricsTests
{
    private readonly OpenTelemetryOptions<FakeChangeSet> _options = new($"jasperfx.tests.otel.{Guid.NewGuid():N}");

    [Fact]
    public void counts_each_appended_event_with_the_tags_the_store_named()
    {
        var measured = new List<(long Value, string? EventType, string? TenantId)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == _options.Meter) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var all = tags.ToArray();
            measured.Add((value,
                all.Single(x => x.Key == OtelConstants.EventType).Value as string,
                all.Single(x => x.Key == OtelConstants.TenantId).Value as string));
        });
        listener.Start();

        _options.ExportEventCounterOnChangeSets("fake.event.append", e => new TagList
        {
            { OtelConstants.EventType, e.EventTypeName }, { OtelConstants.TenantId, e.TenantId }
        });

        _options.RecordCommit(new FakeChangeSet(eventNamed("started", "green"), eventNamed("arrived", "blue")));

        measured.ShouldBe([(1L, "started", "green"), (1L, "arrived", "blue")]);
    }

    [Fact]
    public void a_commit_with_no_events_records_nothing()
    {
        var measured = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == _options.Meter) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => measured++);
        listener.Start();

        _options.ExportEventCounterOnChangeSets("fake.event.append", _ => default);

        _options.RecordCommit(new FakeChangeSet());

        measured.ShouldBe(0);
    }

    [Fact]
    public void the_unit_defaults_to_events()
    {
        Instrument? published = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter == _options.Meter) published = instrument;
        };
        listener.Start();

        _options.ExportEventCounterOnChangeSets("fake.event.append", _ => default);

        published!.Unit.ShouldBe("events");
    }

    private static IEvent eventNamed(string eventTypeName, string tenantId)
    {
        var e = Substitute.For<IEvent>();
        e.EventTypeName.Returns(eventTypeName);
        e.TenantId.Returns(tenantId);
        return e;
    }

    public sealed class FakeChangeSet(params IEvent[] events) : IUnitOfWorkChangeSet
    {
        public IEnumerable<IEvent> GetEvents() => events;

        public IEnumerable<StreamAction> GetStreams() => [];
    }
}
