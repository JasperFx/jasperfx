#nullable enable
using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class SubscriptionMetrics: ISubscriptionMetrics
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _processed;
    private readonly Histogram<long> _gap;
    private readonly Uri _databaseUri;
    private readonly Counter<long> _skipped;

    // marten#6003
    private readonly Counter<long> _parked;
    private readonly Counter<long> _unparked;
    private readonly Counter<long> _heldEvents;
    private int _parkedCurrent;

    public SubscriptionMetrics(IEventStore store, ShardName name, IEventDatabase database) : this(store, name,
        database.DatabaseUri)
    {
        
    }
    
    public SubscriptionMetrics(IEventStore store, ShardName name, Uri databaseUri)
    {
        _activitySource = store.ActivitySource;
        _meter = store.Meter;
        Name = name;

        var identifier = $"{store.MetricsPrefix}.{name.Name.ToLowerInvariant()}.{name.ShardKey.ToLowerInvariant()}";
        
        _processed = _meter.CreateCounter<long>(
            $"{identifier}.processed");

        GapMetricName = $"{identifier}.gap";
        _gap = _meter.CreateHistogram<long>(GapMetricName);

        SkippedMetricName = $"{identifier}.skipped";

        _skipped = _meter.CreateCounter<long>(SkippedMetricName, "number", "Skipped Events");

        // marten#6003: parked identities
        ParkedMetricName = $"{identifier}.parked";
        _parked = _meter.CreateCounter<long>(ParkedMetricName, "identities", "Parked identities");
        UnparkedMetricName = $"{identifier}.unparked";
        _unparked = _meter.CreateCounter<long>(UnparkedMetricName, "identities", "Unparked identities");
        HeldEventsMetricName = $"{identifier}.parked.held_events";
        _heldEvents = _meter.CreateCounter<long>(HeldEventsMetricName, "events",
            "Events held back for parked identities");
        ParkedCurrentMetricName = $"{identifier}.parked.current";

        ExecutionSpanName = $"{identifier}.page.execution";
        LoadingSpanName = $"{identifier}.page.loading";
        GroupingSpanName = $"{identifier}.page.grouping";

        _databaseUri = databaseUri;
    }

    public string SkippedMetricName { get; }

    /// <summary>marten#6003: counter of identities parked.</summary>
    public string ParkedMetricName { get; }

    /// <summary>marten#6003: counter of identities unparked, tagged with the outcome.</summary>
    public string UnparkedMetricName { get; }

    /// <summary>marten#6003: counter of events held back for parked identities.</summary>
    public string HeldEventsMetricName { get; }

    /// <summary>marten#6003: gauge of the identities the shard holds parked.</summary>
    public string ParkedCurrentMetricName { get; }

    public void IncrementParked(int count)
    {
        _parked.Add(count, new TagList { { OtelConstants.DatabaseUri, _databaseUri } });
    }

    public void IncrementUnparked(string outcome)
    {
        _unparked.Add(1, new TagList
        {
            { OtelConstants.DatabaseUri, _databaseUri },
            { "outcome", outcome }
        });
    }

    public void IncrementHeldEvents(int count)
    {
        _heldEvents.Add(count, new TagList { { OtelConstants.DatabaseUri, _databaseUri } });
    }

    private int _parkedGauge;

    public void UpdateParkedCount(int count)
    {
        Volatile.Write(ref _parkedCurrent, count);

        // Created on first use: a shard that never parks, and every description of one, registers no gauge
        if (Interlocked.Exchange(ref _parkedGauge, 1) == 0)
        {
            _meter.CreateObservableGauge(ParkedCurrentMetricName,
                () => new Measurement<int>(Volatile.Read(ref _parkedCurrent),
                    new KeyValuePair<string, object?>(OtelConstants.DatabaseUri, _databaseUri)),
                "identities", "Identities the shard holds parked");
        }
    }

    public string GapMetricName { get; }

    public string LoadingSpanName { get; }
    public string GroupingSpanName { get; }

    public Activity? TrackExecution(EventRange page)
    {
        var activity = _activitySource.StartActivity(ExecutionSpanName, ActivityKind.Internal);
        activity?.AddTag("page.size", page.Events.Count);
        activity?.AddTag("event.floor", page.SequenceFloor);
        activity?.AddTag("event.ceiling", page.SequenceCeiling);
        activity?.AddTag(OtelConstants.DatabaseUri, _databaseUri);
        

        return activity;
    }

    public Activity? TrackGrouping(EventRange page)
    {
        var activity = _activitySource.StartActivity(GroupingSpanName, ActivityKind.Internal);
        activity?.AddTag("page.size", page.Events.Count);
        activity?.AddTag("event.floor", page.SequenceFloor);
        activity?.AddTag("event.ceiling", page.SequenceCeiling);
        activity?.AddTag(OtelConstants.DatabaseUri, _databaseUri);

        return activity;
    }

    public Activity? TrackLoading(EventRequest request)
    {
        var activity = _activitySource.StartActivity(LoadingSpanName, ActivityKind.Internal);
        activity?.AddTag("event.floor", request.Floor);
        activity?.AddTag(OtelConstants.DatabaseUri, _databaseUri);

        return activity;
    }

    public void IncrementSkips()
    {
        _skipped.Add(1, new TagList
        {
            {OtelConstants.DatabaseUri, _databaseUri}
        });
    }

    public void UpdateGap(long highWaterMark, long lastCeiling)
    {
        _gap.Record(highWaterMark - lastCeiling, new KeyValuePair<string, object?>(OtelConstants.DatabaseUri, _databaseUri));
    }

    public void UpdateProcessed(long count)
    {
        _processed.Add(count, new TagList
        {
            {OtelConstants.DatabaseUri, _databaseUri}
        });
    }

    public string ExecutionSpanName { get; }

    public ShardName Name { get; }
}
