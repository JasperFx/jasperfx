using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Descriptors;

public class SubscriptionDescriptor : OptionsDescription
{
    public SubscriptionType SubscriptionType { get; }
    
    

    [JsonConstructor]
    public SubscriptionDescriptor(SubscriptionType subscriptionType)
    {
        SubscriptionType = subscriptionType;
    }

    public SubscriptionDescriptor(ISubscriptionSource subject, IEventStore store) : base(subject)
    {
        SubscriptionType = subject.Type;
        Name = subject.Name;
        Version = subject.Version;
        ShardNames = subject.ShardNames();
        Lifecycle = subject.Lifecycle;

        if (Lifecycle == ProjectionLifecycle.Async)
        {
            foreach (var shardName in ShardNames)
            {
                var metrics = new SubscriptionMetrics(store, shardName, "database://database".ToUri());
                _metrics.Add(new MetricDescriptor(metrics.GapMetricName, MetricsType.Histogram)
                {
                    DatabaseCardinality = store.DatabaseCardinality,
                    HasMultipleTenants = store.HasMultipleTenants,
                    Units = "Events"
                });
            
                _activities.Add(new ActivitySpanDescriptor(metrics.ExecutionSpanName));
                _activities.Add(new ActivitySpanDescriptor(metrics.LoadingSpanName));
                _activities.Add(new ActivitySpanDescriptor(metrics.GroupingSpanName));
            }
        }
    }

    private readonly List<ActivitySpanDescriptor> _activities = new();

    public ActivitySpanDescriptor[] ActivitySpans
    {
        get
        {
            return _activities.ToArray();
        }
        set
        {
            _activities.Clear();
            _activities.AddRange(value);
        }
    }

    private readonly List<MetricDescriptor> _metrics = new();

    public MetricDescriptor[] Metrics
    {
        get
        {
            return _metrics.ToArray();
        }
        set
        {
            _metrics.Clear();
            _metrics.AddRange(value);
        }
    }

    public ProjectionLifecycle Lifecycle { get; set; }
    
    public ShardName[] ShardNames { get; set; }

    public string Name { get; set; }
    public uint Version { get; set; }

    public List<EventDescriptor> Events { get; set; } = new();
}