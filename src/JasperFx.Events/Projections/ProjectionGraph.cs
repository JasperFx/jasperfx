using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Subscriptions;

namespace JasperFx.Events.Projections;

public abstract class ProjectionGraph<TProjection, TOperations, TQuerySession> : DaemonSettings 
    where TOperations : TQuerySession, IStorageOperations
    where TProjection : IJasperFxProjection<TOperations>
{
    private readonly IEventRegistry _events;
    private readonly Dictionary<Type, object> _liveAggregateSources = new();
    private readonly HashSet<Type> _discoveredAggregateTypes = new();
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
        var seen = new HashSet<Type>();

        foreach (var kv in _liveAggregators.Enumerate())
        {
            if (seen.Add(kv.Key)) yield return kv.Key;
        }

        foreach (var projection in All.OfType<IAggregateProjection>())
        {
            if (seen.Add(projection.AggregateType)) yield return projection.AggregateType;
        }

        foreach (var type in _liveAggregateSources.Keys)
        {
            if (seen.Add(type)) yield return type;
        }

        foreach (var type in _discoveredAggregateTypes)
        {
            if (seen.Add(type)) yield return type;
        }
    }
    
    public bool TryFindAggregate(Type documentType, [NotNullWhen(true)]out IAggregateProjection? projection)
    {
        projection = All.OfType<IAggregateProjection>().FirstOrDefault(x => x.AggregateType == documentType);

        if (projection == null)
        {
            var composite = All.OfType<CompositeProjection<TOperations, TQuerySession>>()
                .FirstOrDefault(x => x.PublishedTypes().Contains(documentType));

            if (composite is not null)
            {
                projection = composite.AllChildren().OfType<IAggregateProjection>()
                    .FirstOrDefault(x => x.AggregateType == documentType);
            }
        }
        
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
        var optionsFromComposites = All
            .OfType<CompositeProjection<TOperations, TQuerySession>>()
            .SelectMany(x => x.AllChildren())
            .OfType<IAggregatorSource<TQuerySession>>();
        
        var candidate = All
            .OfType<IAggregatorSource<TQuerySession>>()
            .Concat(optionsFromComposites) 
            .FirstOrDefault(x => x.AggregateType == typeof(T));
        
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


    // Cross-store cache of evolver-attribute discovery. Multi-store apps create one
    // ProjectionGraph per IDocumentStore but iterate the same set of loaded
    // assemblies; without this cache each new store re-pays the assembly walk plus
    // the GetCustomAttributes call per assembly.
    //
    // Keyed by Assembly identity so we never re-scan an assembly we've seen,
    // regardless of which store made the call. Empty results are cached too --
    // the framework assemblies that dominate the loaded list never have user
    // attributes and we want to avoid re-asking them.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, IReadOnlyList<Type>>
        _evolverDiscoveryCache = new();

    /// <summary>
    /// Scan assemblies for GeneratedEvolverAttribute registrations and record
    /// discovered aggregate types. This ensures that self-aggregating types with
    /// source-generated evolvers are reported by AllAggregateTypes() and will
    /// use the efficient generated evolver when aggregated at runtime.
    /// </summary>
    public void DiscoverGeneratedEvolvers(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            // Framework / library assemblies are by far the biggest slice of the
            // loaded-assembly list in a typical app and structurally cannot carry
            // a user-defined [GeneratedEvolverAttribute]. Skip them before paying
            // for the GetCustomAttributes reflection call.
            if (IsAssemblyKnownToHaveNoEvolvers(assembly))
            {
                continue;
            }

            var types = _evolverDiscoveryCache.GetOrAdd(assembly, ReadEvolverAggregateTypes);
            for (var i = 0; i < types.Count; i++)
            {
                _discoveredAggregateTypes.Add(types[i]);
            }
        }
    }

    private static IReadOnlyList<Type> ReadEvolverAggregateTypes(Assembly assembly)
    {
        try
        {
            var attrs = assembly.GetCustomAttributes<GeneratedEvolverAttribute>();
            // Materialize so the cached value is independent of the assembly's
            // attribute caching. Most assemblies hit Array.Empty<> here.
            List<Type>? aggregateTypes = null;
            foreach (var attr in attrs)
            {
                aggregateTypes ??= new List<Type>();
                aggregateTypes.Add(attr.AggregateType);
            }

            return aggregateTypes ?? (IReadOnlyList<Type>)Array.Empty<Type>();
        }
        catch
        {
            // Match historical behavior: an assembly that throws on attribute
            // load is treated as having no evolvers and is cached so we don't
            // ask again.
            return Array.Empty<Type>();
        }
    }

    /// <summary>
    /// Cheap pre-filter for assemblies that cannot carry user-defined
    /// <see cref="GeneratedEvolverAttribute"/> registrations -- the framework BCL,
    /// well-known infrastructure libraries, and our own assemblies. Skipping these
    /// before the <see cref="System.Reflection.CustomAttributeExtensions.GetCustomAttributes{T}(Assembly)"/>
    /// call cuts the per-cold-start work proportional to "framework assemblies in
    /// the AppDomain", which on a typical .NET host is the bulk of the list.
    /// </summary>
    private static bool IsAssemblyKnownToHaveNoEvolvers(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name == null) return false;

        return name.StartsWith("System", StringComparison.Ordinal)
            || name.StartsWith("Microsoft", StringComparison.Ordinal)
            || name.StartsWith("netstandard", StringComparison.Ordinal)
            || name.Equals("mscorlib", StringComparison.Ordinal)
            || name.Equals("WindowsBase", StringComparison.Ordinal)
            || name.StartsWith("JasperFx", StringComparison.Ordinal)
            || name.StartsWith("Marten", StringComparison.Ordinal)
            || name.StartsWith("Wolverine", StringComparison.Ordinal)
            || name.StartsWith("Weasel", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal)
            || name.StartsWith("Newtonsoft.Json", StringComparison.Ordinal)
            || name.StartsWith("Polly", StringComparison.Ordinal)
            || name.StartsWith("Spectre.Console", StringComparison.Ordinal)
            || name.StartsWith("xunit", StringComparison.Ordinal)
            || name.StartsWith("FluentAssertions", StringComparison.Ordinal)
            || name.StartsWith("ImTools", StringComparison.Ordinal)
            || name.StartsWith("FastExpressionCompiler", StringComparison.Ordinal);
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

