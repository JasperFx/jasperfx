#nullable enable
using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Core;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class MetricsNaming
{
    [Obsolete("Eliminate this. Tag the database instead")]
    public string DefaultDatabaseIdentifier { get; init; } = "Default";
    public string DatabaseIdentifier { get; init; }
    public string MetricsPrefix { get; init; }
}

public class SubscriptionMetrics: ISubscriptionMetrics
{
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly string _databaseName;
    private readonly Counter<long> _processed;
    private readonly Histogram<long> _gap;

    public SubscriptionMetrics(ActivitySource activitySource, Meter meter, ShardName name, MetricsNaming naming)
    {
        _activitySource = activitySource;
        _meter = meter;
        _databaseName = naming.DatabaseIdentifier;
        Name = name;

        var identifier = $"marten.{name.Name.ToLower()}.{name.ShardKey.ToLower()}";
        
        // TODO -- let's reevaluate this!
        var databaseIdentifier = naming.DatabaseIdentifier.EqualsIgnoreCase(naming.DefaultDatabaseIdentifier)
            ? identifier
            : $"{naming.MetricsPrefix}.{naming.DatabaseIdentifier.ToLower()}.{name.Name.ToLower()}.{name.ShardKey.ToLower()}";


        _processed = meter.CreateCounter<long>(
            $"{identifier}.processed");

        _gap = meter.CreateHistogram<long>($"{databaseIdentifier}.gap");


        ExecutionSpanName = $"{identifier}.page.execution";
        LoadingSpanName = $"{identifier}.page.loading";
        GroupingSpanName = $"{identifier}.page.grouping";
    }

    public string LoadingSpanName { get; }
    public string GroupingSpanName { get; }

    public Activity? TrackExecution(EventRange page)
    {
        var activity = _activitySource.StartActivity(ExecutionSpanName, ActivityKind.Internal);
        activity?.AddTag("page.size", page.Events.Count);
        activity?.AddTag("event.floor", page.SequenceFloor);
        activity?.AddTag("event.ceiling", page.SequenceCeiling);
        activity?.AddTag("marten.database", _databaseName);

        return activity;
    }

    public Activity? TrackGrouping(EventRange page)
    {
        var activity = _activitySource.StartActivity(GroupingSpanName, ActivityKind.Internal);
        activity?.AddTag("page.size", page.Events.Count);
        activity?.AddTag("event.floor", page.SequenceFloor);
        activity?.AddTag("event.ceiling", page.SequenceCeiling);
        activity?.AddTag("marten.database", _databaseName);

        return activity;
    }

    public Activity? TrackLoading(EventRequest request)
    {
        var activity = _activitySource.StartActivity(LoadingSpanName, ActivityKind.Internal);
        activity?.AddTag("event.floor", request.Floor);
        activity?.AddTag("marten.database", _databaseName);

        return activity;
    }

    public void UpdateGap(long highWaterMark, long lastCeiling)
    {
        _gap.Record(highWaterMark - lastCeiling);
    }

    public void UpdateProcessed(long count)
    {
        _processed.Add(count, new TagList
        {
            {"marten.database", _databaseName}
        });
    }

    public string ExecutionSpanName { get; }

    public ShardName Name { get; }
}
