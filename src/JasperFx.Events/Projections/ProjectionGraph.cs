using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Projections;

public abstract class ProjectionGraph<TProjection, TOperations, TQuerySession> : DaemonSettings 
    where TOperations : TQuerySession, IStorageOperations
    where TProjection : IJasperFxProjection<TOperations>
{
    private readonly IEventRegistry _events;
    private readonly Dictionary<Type, object> _liveAggregateSources = new();
    private ImHashMap<Type, object> _liveAggregators = ImHashMap<Type, object>.Empty;
    private readonly List<ISubscriptionSource<TOperations, TQuerySession>> _subscriptions = new();
    private Lazy<Dictionary<string, AsyncShard<TOperations, TQuerySession>>> _asyncShards = null!;

    protected ProjectionGraph(IEventRegistry events, string otelPrefixName)
    {
        OtelPrefix = otelPrefixName;
        _events = events;
    }

    /// <summary>
    /// Async daemon error handling policies while running in a rebuild mode. The defaults
    /// are to *not* skip any errors
    /// </summary>
    [ChildDescription]
    public ErrorHandlingOptions RebuildErrors { get; } = new();
    
    
    // This has to be public for CritterStackPro
    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> AllShards()
    {
        return _asyncShards.Value.Values.ToList();
    }

    /// <summary>
    /// Async daemon error handling polices while running continuously. The defaults
    /// are to skip serialization errors, unknown events, and apply errors
    /// </summary>
    [ChildDescription]
    public ErrorHandlingOptions Errors { get; } = new()
    {
        SkipApplyErrors = true,
        SkipSerializationErrors = true,
        SkipUnknownEvents = true
    };

    public List<IProjectionSource<TOperations, TQuerySession>> All { get; } = new();
    
    public bool HasAnyAsyncProjections()
    {
        return All.Any(x => x.Lifecycle == ProjectionLifecycle.Async) || _subscriptions.Any();
    }

    public IEnumerable<Type> AllAggregateTypes()
    {
        foreach (var kv in _liveAggregators.Enumerate()) yield return kv.Key;

        foreach (var projection in All.OfType<IAggregateProjection>()) yield return projection.AggregateType;
    }
    
    public bool TryFindAggregate(Type documentType, [NotNullWhen(true)]out IAggregateProjection? projection)
    {
        projection = All.OfType<IAggregateProjection>().FirstOrDefault(x => x.AggregateType == documentType);
        return projection != null;
    }

    protected abstract void onAddProjection(object projection);

    /// <summary>
    /// Register a projection to the event store configuration
    /// </summary>
    /// <param name="projection">Value values are Inline/Async, The default is Inline</param>
    /// <param name="lifecycle"></param>
    /// <param name="projectionName">
    ///     Overwrite the named identity of this projection. This is valuable if using the projection
    ///     asynchronously
    /// </param>
    /// <param name="asyncConfiguration">
    ///     Optional configuration including teardown instructions for the usage of this
    ///     projection within the async projection daempon
    /// </param>
    public void Add(
        TProjection projection,
        ProjectionLifecycle lifecycle,
        string? projectionName = null,
        Action<AsyncOptions>? asyncConfiguration = null
    )
    {
        if (lifecycle == ProjectionLifecycle.Live)
        {
            if (!projection.GetType().Closes(typeof(IAggregator<,>)))
            {
                throw new ArgumentOutOfRangeException(nameof(lifecycle),
                    $"{nameof(ProjectionLifecycle.Live)} cannot be used for IProjection");
            }
        }

        if (projection is ProjectionBase p)
        {
            p.AssembleAndAssertValidity();
            p.Lifecycle = lifecycle;

            foreach (var eventType in p.IncludedEventTypes)
            {
                _events.AddEventType(eventType);
            }
        }

        if (projection is IProjectionSource<TOperations, TQuerySession> source)
        {
            asyncConfiguration?.Invoke(source.Options);
            All.Add(source);
            
        }
        else
        {
            var wrapper = new ProjectionWrapper<TOperations, TQuerySession>(projection, lifecycle);
            if (projectionName.IsNotEmpty())
            {
                ((ProjectionBase)wrapper).Name = projectionName;
            }

            asyncConfiguration?.Invoke(wrapper.Options);
            All.Add(wrapper);
        }
    }
    
    /// <summary>
    /// Register a projection to the sytem's configuration
    /// </summary>
    /// <param name="projection">Value values are Inline/Async, The default is Inline</param>
    /// <param name="lifecycle"></param>
    /// <param name="projectionName">
    ///     Overwrite the named identity of this projection. This is valuable if using the projection
    ///     asynchronously
    /// </param>
    /// <param name="asyncConfiguration">
    ///     Optional configuration including teardown instructions for the usage of this
    ///     projection within the async projection daempon
    /// </param>
    public void Register(
        IProjectionSource<TOperations, TQuerySession> source,
        ProjectionLifecycle lifecycle,
        Action<AsyncOptions>? asyncConfiguration = null
    )
    {
        if (source is ProjectionBase p)
        {
            p.AssembleAndAssertValidity();
            p.Lifecycle = lifecycle;
        }
        
        onAddProjection(source);

        asyncConfiguration?.Invoke(source.Options);
        All.Add(source);
    }
    
    /// <summary>
    /// Add a projection that will be executed inline
    /// </summary>
    /// <param name="source">A projection or projection source object</param>
    /// <param name="lifecycle">Optionally override the lifecycle of this projection. The default is Inline</param>
    /// <param name="asyncConfiguration">Use it to define behaviour during projection rebuilds</param>
    public void Add(
        IProjectionSource<TOperations, TQuerySession> source,
        ProjectionLifecycle lifecycle,
        Action<AsyncOptions>? asyncConfiguration = null
    )
    {
        Register(source, lifecycle, asyncConfiguration);
    }

    /// <summary>
    /// Register an aggregate projection that should be evaluated inline
    /// </summary>
    /// <typeparam name="TProjectionType">Projection type</typeparam>
    /// <param name="lifecycle">Optionally override the ProjectionLifecycle</param>
    /// <param name="asyncConfiguration">Use it to define behaviour during projection rebuilds</param>
    public void Add<TProjectionType>(
        ProjectionLifecycle lifecycle,
        Action<AsyncOptions>? asyncConfiguration = null
    )
        where TProjectionType : ProjectionBase, IProjectionSource<TOperations, TQuerySession>, new()
    {
        var projection = new TProjectionType { Lifecycle = lifecycle };

        asyncConfiguration?.Invoke(projection.Options);

        projection.AssembleAndAssertValidity();

        All.Add(projection);
    }
    
    /// <summary>
    /// Add a subscription to this event store
    /// </summary>
    /// <param name="subscription"></param>
    protected void registerSubscription(ISubscriptionSource<TOperations, TQuerySession> subscription)
    {
        _subscriptions.Add(subscription);
    }
    
    public bool IsActive()
    {
        return All.Any() || _subscriptions.Any();
    }
    
    public IAggregator<T, TQuerySession> AggregatorFor<T>() where T : class
    {
        if (_liveAggregators.TryFind(typeof(T), out var aggregator))
        {
            return (IAggregator<T, TQuerySession>)aggregator;
        }

        aggregator = All.OfType<IAggregator<T, TQuerySession>>().FirstOrDefault();
        if (aggregator != null)
        {
            _liveAggregators = _liveAggregators.AddOrUpdate(typeof(T), aggregator);
            return (IAggregator<T, TQuerySession>)aggregator;
        }

        var source = tryFindProjectionSourceForAggregateType<T>();
        if (source is ProjectionBase p) p.AssembleAndAssertValidity();

        aggregator = source.As<IAggregatorSource<TQuerySession>>().Build<T>();
        _liveAggregators = _liveAggregators.AddOrUpdate(typeof(T), aggregator);

        return (IAggregator<T, TQuerySession>)aggregator;
    }
    
    private IAggregatorSource<TQuerySession> tryFindProjectionSourceForAggregateType<T>() where T : class
    {
        var candidate = All.OfType<IAggregatorSource<TQuerySession>>().FirstOrDefault(x => x.AggregateType == typeof(T));
        if (candidate != null)
        {
            return candidate;
        }

        if (!_liveAggregateSources.TryGetValue(typeof(T), out var source))
        {
            if (_events is IAggregationSourceFactory<TQuerySession> factory)
            {
                source = factory.Build<T>();
            }
        }

        return source as IAggregatorSource<TQuerySession>;
    }

    public string[] AllProjectionNames()
    {
        return All.Select(x => $"'{x.Name}'").Concat(_subscriptions.Select(x => $"'{x.Name}'")).ToArray();
    }

    public IEnumerable<Type> AllPublishedTypes()
    {
        return All.Where(x => x.Lifecycle != ProjectionLifecycle.Live).SelectMany(x => x.PublishedTypes()).Distinct();
    }

    public ShardName[] AsyncShardsPublishingType(Type aggregationType)
    {
        var sources = All.Where(x => x.Lifecycle == ProjectionLifecycle.Async && x.PublishedTypes().Contains(aggregationType)).Select(x => x.Name).ToArray();
        return _asyncShards.Value.Values.Where(x => sources.Contains(x.Name.Name)).Select(x => x.Name).ToArray();
    }
    
    public bool TryFindAsyncShard(string projectionOrShardName, [NotNullWhen(true)]out AsyncShard<TOperations, TQuerySession>? shard)
    {
        return _asyncShards.Value.TryGetValue(projectionOrShardName, out shard);
    }

    public bool TryFindProjection(string projectionName, [NotNullWhen(true)]out IProjectionSource<TOperations, TQuerySession>? source)
    {
        source = All.FirstOrDefault(x => x.Name.EqualsIgnoreCase(projectionName));
        return source != null;
    }

    internal bool TryFindSubscription(string projectionName, [NotNullWhen(true)]out ISubscriptionSource<TOperations, TQuerySession>? source)
    {
        source = _subscriptions.FirstOrDefault(x => x.Name.EqualsIgnoreCase(projectionName));
        return source != null;
    }

    public void Describe(EventStoreUsage usage, IEventStore store)
    {
        foreach (var source in _subscriptions)
        {
            usage.Subscriptions.Add(new SubscriptionDescriptor(source, store));
        }

        foreach (var eventType in _subscriptions.OfType<EventFilterable>().Concat(All.OfType<EventFilterable>()).SelectMany(x => x.IncludedEventTypes))
        {
            _events.AddEventType(eventType);
        }

        foreach (var source in All)
        {
            usage.Subscriptions.Add(source.Describe(store));
        }
    }


    public void AssertValidity<T>(T options)
    {
        var duplicateNames = All.Select(x => x.Name).Concat(_subscriptions.Select(x => x.Name))
            .GroupBy(x => x)
            .Where(x => x.Count() > 1)
            .Select(group =>
                $"Duplicate projection or subscription name '{group.Key}': {group.Select(x => x.ToString()).Join(", ")}. You can set the 'Name' property on the projection or subscription to override the default names and thus avoid duplicates.")
            .ToArray();

        if (duplicateNames.Any())
        {
            throw new DuplicateSubscriptionNamesException(duplicateNames.Join("; "));
        }

        var messages = All.Concat(_liveAggregateSources.Values)
            .OfType<IValidatedProjection<T>>()
            .Distinct()
            .SelectMany(x => x.ValidateConfiguration(options))
            .ToArray();

        _asyncShards = new Lazy<Dictionary<string, AsyncShard<TOperations, TQuerySession>>>(() =>
        {
            return All
                .Where(x => x.Lifecycle == ProjectionLifecycle.Async)
                .SelectMany(x => x.Shards())
                .Concat(_subscriptions.SelectMany(x => x.Shards()))
                .ToDictionary(x => x.Name.Identity);
        });

        if (messages.Any())
        {
            throw new InvalidProjectionException(messages);
        }
    }
}