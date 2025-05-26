#nullable enable
using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class MetricsNaming
{
    public Uri DatabaseUri { get; init; }
    public string MetricsPrefix { get; init; }
}

public class SubscriptionMetrics: ISubscriptionMetrics
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly MetricsNaming _naming;
    private readonly Counter<long> _processed;
    private readonly Histogram<long> _gap;
    private readonly Uri _databaseUri;

    public SubscriptionMetrics(ActivitySource activitySource, Meter meter, ShardName name, MetricsNaming naming)
    {
        _activitySource = activitySource;
        _meter = meter;
        _naming = naming;
        Name = name;

        var identifier = $"{naming.MetricsPrefix}.{name.Name.ToLower()}.{name.ShardKey.ToLower()}";
        
        _processed = meter.CreateCounter<long>(
            $"{identifier}.processed");

        _gap = meter.CreateHistogram<long>($"{identifier}.gap");

        ExecutionSpanName = $"{identifier}.page.execution";
        LoadingSpanName = $"{identifier}.page.loading";
        GroupingSpanName = $"{identifier}.page.grouping";

        _databaseUri = naming.DatabaseUri;
    }

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
