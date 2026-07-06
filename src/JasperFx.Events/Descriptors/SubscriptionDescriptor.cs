using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Aggregation;
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

    public SubscriptionDescriptor(ISubscriptionSource subject, IEventStore store)
    {
        // Explicitly build — no reflection via base(subject)
        Subject = subject.GetType().FullName ?? subject.GetType().Name;
        SubscriptionType = subject.Type;
        Name = subject.Name;
        Version = subject.Version;
        ShardNames = subject.ShardNames();
        Lifecycle = subject.Lifecycle;

        // The CLR type that implements this projection/subscription, plus — for self-aggregating
        // projections (Snapshot<T> / SingleStreamProjection<T>) — the aggregate/document type, so
        // consumers can display "what .NET type implements this projection". This is diagnostics-only
        // metadata, so a source that fails to supply a type leaves the property null rather than
        // blowing up the whole Describe() pipeline.
        if (subject.ImplementationType is { } implementationType)
        {
            ImplementationType = TypeDescriptor.For(implementationType);
        }

        if (subject is IAggregateProjection { AggregateType: { } aggregateType })
        {
            AggregateType = TypeDescriptor.For(aggregateType);
        }

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

    public new MetricDescriptor[] Metrics
    {
        get => _metrics.ToArray();
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

    /// <summary>The CLR type that implements this projection/subscription.</summary>
    public TypeDescriptor? ImplementationType { get; set; }

    /// <summary>
    /// For self-aggregating projections (Snapshot&lt;T&gt; / SingleStreamProjection&lt;T&gt;), the
    /// aggregate/document type T. Null for non-aggregating subscriptions/projections.
    /// </summary>
    public TypeDescriptor? AggregateType { get; set; }

    /// <summary>
    /// Agent URIs that would be assigned for each shard of this subscription
    /// as per EventSubscriptionAgentFamily conventions
    /// </summary>
    public string[] AgentUris { get; set; } = [];
}