using System.Collections.Concurrent;
using ImTools;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;


public partial class JasperFxAsyncDaemon<TOperations, TQuerySession, TProjection> : IObserver<ShardState>, IDaemonRuntime
    where TOperations : TQuerySession, IStorageOperations
    where TProjection : IJasperFxProjection<TOperations>
{
    private readonly IEventStore<TOperations, TQuerySession> _store;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ProjectionGraph<TProjection, TOperations, TQuerySession> _projections;
    private ImHashMap<string, ISubscriptionAgent> _agents = ImHashMap<string, ISubscriptionAgent>.Empty;
    private CancellationTokenSource _cancellation = new();
    private readonly HighWaterAgent _highWater;
    private readonly IDisposable _breakSubscription;
    private RetryBlock<DeadLetterEvent> _deadLetterBlock;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public JasperFxAsyncDaemon(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory, IHighWaterDetector detector, ProjectionGraph<TProjection, TOperations, TQuerySession> projections)
    {
        Database = database;
        _store = store;
        _loggerFactory = loggerFactory;
        _projections = projections;
        Logger = loggerFactory.CreateLogger(GetType());
        Tracker = Database.Tracker;
        _highWater = new HighWaterAgent(store.Meter, detector, Tracker, loggerFactory.CreateLogger<HighWaterAgent>(), projections, _cancellation.Token);

        _breakSubscription = database.Tracker.Subscribe(this);

        _deadLetterBlock = buildDeadLetterBlock();
    }
    
    public JasperFxAsyncDaemon(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, IHighWaterDetector detector, ProjectionGraph<TProjection, TOperations, TQuerySession> projections)
    {
        Database = database;
        _store = store;
        _projections = projections;
        _loggerFactory = null;
        Logger = logger;
        Tracker = Database.Tracker;
        _highWater = new HighWaterAgent(store.Meter, detector, Tracker, logger, _projections, _cancellation.Token);

        _breakSubscription = database.Tracker.Subscribe(this);

        _deadLetterBlock = buildDeadLetterBlock();
    }

    private RetryBlock<DeadLetterEvent> buildDeadLetterBlock()
    {
        return new RetryBlock<DeadLetterEvent>(async (deadLetterEvent, token) =>
        {
            // More important to end cleanly
            if (token.IsCancellationRequested) return;

            await Database.StoreDeadLetterEventAsync(_store, deadLetterEvent, token).ConfigureAwait(false);
        }, Logger, _cancellation.Token);
    }

    public IEventDatabase Database { get; }

    public ILogger Logger { get; }

    public void Dispose()
    {
        _cancellation?.Dispose();
        _highWater?.Dispose();
        _breakSubscription.Dispose();
        _deadLetterBlock.Dispose();
    }

    public ShardStateTracker Tracker { get; }
    public bool IsRunning => _highWater.IsRunning;


    private async Task<bool> tryStartAgentAsync(ISubscriptionAgent agent, ShardExecutionMode mode)
    {
        // Be idempotent, don't start an agent that is already running
        if (_agents.TryFind(agent.Name.Identity, out var running) && running.Status == AgentStatus.Running)
        {
            return false;
        }

        // Lock
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            // Be idempotent, don't start an agent that is already running now that we have the lock.
            if (_agents.TryFind(agent.Name.Identity, out running) && running.Status == AgentStatus.Running)
            {
                return false;
            }

            var highWaterMark = HighWaterMark();
            var position = await agent
                .Options
                .DetermineStartingPositionAsync(highWaterMark, agent.Name, mode, Database, _cancellation.Token)
                .ConfigureAwait(false);

            if (position.ShouldUpdateProgressFirst)
            {
                await _store.RewindSubscriptionProgressAsync(Database, agent.Name.Identity, _cancellation.Token, position.Floor).ConfigureAwait(false);
            }

            var errorOptions = mode == ShardExecutionMode.Continuous
                ? _store.ContinuousErrors
                : _store.RebuildErrors;

            await agent.StartAsync(new SubscriptionExecutionRequest(position.Floor, mode, errorOptions, this))
                .ConfigureAwait(false);
            agent.MarkHighWater(highWaterMark);

            _agents = _agents.AddOrUpdate(agent.Name.Identity, agent);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error trying to start agent {ShardName}", agent.Name.Identity);
            return false;
        }
        finally
        {
            _semaphore.Release();
        }

        return true;
    }

    private async Task rebuildAgent(ISubscriptionAgent agent, long highWaterMark, TimeSpan shardTimeout)
    {
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            // Ensure that the agent is stopped if it is already running
            await stopIfRunningAsync(agent.Name.Identity).ConfigureAwait(false);

            var errorOptions = _store.RebuildErrors;

            var request = new SubscriptionExecutionRequest(0, ShardExecutionMode.Rebuild, errorOptions, this);
            await agent.ReplayAsync(request, highWaterMark, shardTimeout).ConfigureAwait(false);

            _agents = _agents.AddOrUpdate(agent.Name.Identity, agent);
        }
        finally
        {
            _semaphore.Release();
        }
    }
    
    
    public async Task StartAgentAsync(string shardName, CancellationToken token)
    {
        if (!_highWater.IsRunning)
        {
            await StartHighWaterDetectionAsync().ConfigureAwait(false);
        }

        // TODO -- DO NOT LIKE THIS. Would rather have an overload that takes ShardName now
        if (!shardName.Contains(":"))
        {
            var shardNames = _store.AllShards().Where(x => x.Name.Name.EqualsIgnoreCase(shardName)).ToArray();
            if (shardNames.Any())
            {
                foreach (var name in shardNames)
                {
                    await StartAgentAsync(name.Name.Identity, token).ConfigureAwait(false);
                }

                return;
            }
        }


        var shard = _store.AllShards().FirstOrDefault(x => x.Name.Identity == shardName);
        if (shard == null)
        {
            throw new ArgumentOutOfRangeException(nameof(shardName),
                $"Unknown shard name '{shardName}'. Value options are {_store.AllShards().Select(x => x.Name.Identity).Join(", ")}");
        }

        var agent = buildAgentForShard(shard);

        var didStart = await tryStartAgentAsync(agent, ShardExecutionMode.Continuous).ConfigureAwait(false);

        if (!didStart && agent is IAsyncDisposable d)
        {
            // Could not be started
            await d.DisposeAsync().ConfigureAwait(false);
        }
    }
    
    public async Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token)
    {
        await StartAgentAsync(name.Identity, token);
        if (_agents.TryFind(name.Identity, out var agent)) return agent;

        // Should not ever happen, but real life man
        throw new Exception("Unable to start a subscription agent for " + name);
    }

    public Task StopAgentAsync(ShardName shardName, Exception? ex = null)
    {
        return StopAgentAsync(shardName.Identity);
    }

    private SubscriptionAgent buildAgentForShard(AsyncShard<TOperations, TQuerySession> shard)
    {
        var execution = _loggerFactory == null ? shard.Factory.BuildExecution(_store, Database, Logger, shard.Name) : shard.Factory.BuildExecution(_store, Database, _loggerFactory, shard.Name);
        var loader = _store.BuildEventLoader(Database, Logger, shard.Filters, shard.Options);

        var metrics = new SubscriptionMetrics(_store, shard.Name, Database);
        
        var agent = new SubscriptionAgent(shard.Name, shard.Options, _store.TimeProvider, loader, execution,
            Database.Tracker, metrics, _loggerFactory?.CreateLogger<SubscriptionAgent>() ?? Logger);
        
        return agent;
    }

    private async Task stopIfRunningAsync(string shardIdentity)
    {
        if (_agents.TryFind(shardIdentity, out var agent))
        {
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(5.Seconds());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _cancellation.Token);

            try
            {
                await agent.StopAndDrainAsync(linked.Token).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop and drain a subscription agent for '{Name}'",
                    agent.Name.Identity);
            }
            finally
            {
                _agents = _agents.Remove(shardIdentity);
            }
        }
    }

    public async Task StopAgentAsync(string shardName, Exception? ex = null)
    {
        if (_agents.TryFind(shardName, out var agent))
        {
            await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            try
            {
                var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(5.Seconds());
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _cancellation.Token);

                try
                {
                    await agent.StopAndDrainAsync(linked.Token).ConfigureAwait(true);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error trying to stop and drain a subscription agent for '{Name}'",
                        agent.Name.Identity);
                }
                finally
                {
                    _agents = _agents.Remove(shardName);

                    if (!_agents.Enumerate().Any() && _highWater.IsRunning)
                    {
                        // Nothing happening, so might as well stop hammering the database!
                        await _highWater.StopAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    public async Task StartAllAsync()
    {
        if (!_highWater.IsRunning)
        {
            await StartHighWaterDetectionAsync().ConfigureAwait(false);
        }

        var agents = new List<ISubscriptionAgent>();

        foreach (var shard in _store.AllShards())
        {
            agents.Add(buildAgentForShard(shard));
        }
        
        foreach (var agent in agents)
        {
            await tryStartAgentAsync(agent, ShardExecutionMode.Continuous).ConfigureAwait(false);
        }
    }

    public async Task StopAllAsync()
    {
        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            await _highWater.StopAsync().ConfigureAwait(false);

            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(5.Seconds());
            try
            {
                var activeAgents = _agents.Enumerate().Select(x => x.Value).Where(x => x.Status == AgentStatus.Running)
                    .ToArray();
                await Parallel.ForEachAsync(activeAgents, cancellation.Token,
                    (agent, t) => new ValueTask(agent.StopAndDrainAsync(t))).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Nothing, you're already trying to get out
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop subscription agents for {Agents}", _agents.Enumerate().Select(x => x.Value.Name.Identity).Join(", "));
            }

            try
            {
                await _deadLetterBlock.DrainAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to finish all outstanding DeadLetterEvent persistence");
            }

            _agents = ImHashMap<string, ISubscriptionAgent>.Empty;

            _cancellation.TryReset();
            _deadLetterBlock = buildDeadLetterBlock();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task StartHighWaterDetectionAsync()
    {
        if (_store.AutoCreateSchemaObjects != AutoCreate.None)
        {
            await Database.EnsureStorageExistsAsync(typeof(IEvent), _cancellation.Token).ConfigureAwait(false);
        }

        await _highWater.StartAsync().ConfigureAwait(false);
    }

    private ConcurrentBag<ShardState>? _shardStateTracker;
    
    public async Task WaitForNonStaleData(TimeSpan timeout)
    {
        _shardStateTracker = new ConcurrentBag<ShardState>();
        
        try
        {
            await Database.WaitForNonStaleProjectionDataAsync(timeout);
        }
        catch (TimeoutException e)
        {
            var exceptions = _shardStateTracker.Select(x => x.Exception).Where(x => x != null).ToArray();
            if (exceptions.Any())
            {
                throw new AggregateException([e, ..exceptions!]);
            }

            throw;
        }
        finally
        {
            _shardStateTracker = null;
        }
        
    }

    public Task WaitForShardToBeRunning(string shardName, TimeSpan timeout)
    {
        if (StatusFor(shardName) == AgentStatus.Running) return Task.CompletedTask;

        Func<ShardState, bool> match = state =>
        {
            if (!state.ShardName.EqualsIgnoreCase(shardName)) return false;

            return state.Action == ShardAction.Started || state.Action == ShardAction.Updated;
        };

        return Tracker.WaitForShardCondition(match, $"Wait for '{shardName}' to be running",timeout);
    }

    public AgentStatus StatusFor(string shardName)
    {
        if (_agents.TryFind(shardName, out var agent))
        {
            return agent.Status;
        }

        return AgentStatus.Stopped;
    }

    public IReadOnlyList<ISubscriptionAgent> CurrentAgents()
    {
        return _agents.Enumerate().Select(x => x.Value).ToList();
    }

    public bool HasAnyPaused()
    {
        return CurrentAgents().Any(x => x.Status == AgentStatus.Paused);
    }

    public void EjectPausedShard(string shardName)
    {
        // Not worried about a lock here.
        _agents = _agents.Remove(shardName);
    }

    public Task PauseHighWaterAgentAsync()
    {
        return _highWater.StopAsync();
    }

    public long HighWaterMark()
    {
        return Tracker.HighWaterMark;
    }

    void IObserver<ShardState>.OnCompleted()
    {
        // Nothing
    }

    void IObserver<ShardState>.OnError(Exception error)
    {
        // Nothing
    }

    void IObserver<ShardState>.OnNext(ShardState value)
    {
        if (value.ShardName == ShardState.HighWaterMark)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Event high water mark detected at {Sequence}", value.Sequence);
            }

            foreach (var agent in CurrentAgents())
            {
                agent.MarkHighWater(value.Sequence);
            }
        }
        
        _shardStateTracker?.Add(value);
    }

    public Task RecordDeadLetterEventAsync(DeadLetterEvent @event)
    {
        return _deadLetterBlock.PostAsync(@event);
    }


    public Task RebuildProjectionAsync(string projectionName, CancellationToken token)
    {
        return RebuildProjectionAsync(projectionName, 5.Minutes(), token);
    }

    public Task RebuildProjectionAsync<TView>(CancellationToken token)
    {
        return RebuildProjectionAsync<TView>(5.Minutes(), token);
    }

    public Task RebuildProjectionAsync(Type projectionType, CancellationToken token)
    {
        return RebuildProjectionAsync(projectionType, 5.Minutes(), token);
    }

    // projectionType can be either the IProjectionSource type, or the aggregate type
    public Task RebuildProjectionAsync(Type projectionType, TimeSpan shardTimeout, CancellationToken token)
    {
        
        var projection = _projections.All.FirstOrDefault(x => x.GetType() == projectionType)
                         ?? _projections.All.FirstOrDefault(x => x.PublishedTypes().Contains(projectionType))
                         ?? _projections.All.FirstOrDefault(x => x is ProjectionWrapper<TOperations, TQuerySession> wrapper && wrapper.ProjectionType == projectionType);

        if (projection == null && projectionType.CanBeCastTo<IProjectionSource<TOperations, TQuerySession>>() &&
            projectionType.HasDefaultConstructor())
        {
            projection = (IProjectionSource<TOperations, TQuerySession>?)Activator.CreateInstance(projectionType);
        }

        if (projection != null)
        {
            return rebuildProjection(projection, shardTimeout, token);
        }

        throw new ArgumentOutOfRangeException("TView",
            $"No registered projection matches the type '{projectionType.FullNameInCode()} or is known to publish that type'. Available projections are {_projections.All.Select(x => x.ToString()!).Join(", ")}");
    }

    public Task RebuildProjectionAsync(string projectionName, TimeSpan shardTimeout, CancellationToken token)
    {
        if (_projections.TryFindProjection(projectionName, out var source))
        {
            return rebuildProjection(source, shardTimeout, token);
        }
        
        throw new ArgumentOutOfRangeException(nameof(projectionName),
        $"No registered projection matches the name '{projectionName}'. Available names are {_projections.AllProjectionNames().Join(", ")}");
    }

    public Task RebuildProjectionAsync<TView>(TimeSpan shardTimeout, CancellationToken token)
    {
        var projectionType = typeof(TView);
        return RebuildProjectionAsync(projectionType, shardTimeout, token);
    }

    private async Task rebuildProjection(IProjectionSource<TOperations, TQuerySession> source, TimeSpan shardTimeout, CancellationToken token)
    {
        await Database.EnsureStorageExistsAsync(typeof(IEvent), token).ConfigureAwait(false);

        var subscriptionName = source.Name;
        Logger.LogInformation("Starting to rebuild Projection {ProjectionName}@{DatabaseIdentifier}",
            subscriptionName, Database.Identifier);

        await stopRunningAgents(subscriptionName).ConfigureAwait(false);

        if (token.IsCancellationRequested) return;

        // Check now regardless
        await _highWater.CheckNowAsync().ConfigureAwait(false);

        // If there's no data, do nothing
        if (Tracker.HighWaterMark == 0)
        {
            Logger.LogInformation("Aborting projection rebuild because the high water mark is 0 (no event data)");
            return;
        }

        if (token.IsCancellationRequested) return;

        var agents = buildAgentsForSubscription(source);
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("No agents were built for subscription " + subscriptionName);
        }

        foreach (var agent in agents)
        {
            Tracker.MarkAsRestarted(agent.Name);
        }

        // Tear down the current state
        await _store.TeardownExistingProjectionProgressAsync(Database, subscriptionName, token).ConfigureAwait(false);

        if (token.IsCancellationRequested)
        {
            return;
        }

        var mark = Tracker.HighWaterMark;

        // Is the shard count the optimal DoP here?
        await Parallel.ForEachAsync(agents,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = agents.Count() },
            async (agent, cancellationToken) =>
            {
                Tracker.MarkAsRestarted(agent.Name);

                await rebuildAgent(agent, mark, shardTimeout).ConfigureAwait(false);
            }).ConfigureAwait(false);

        foreach (var agent in agents)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(shardTimeout);

            try
            {
                await agent.StopAndDrainAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to stop and drain agent {Name} after rebuilding", agent.Name.Identity);
            }
        }
    }

    private async Task stopRunningAgents(string subscriptionName)
    {
        var running = CurrentAgents().Where(x => x.Name.Name == subscriptionName).ToArray();

        await _semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);

        try
        {
            foreach (var agent in running)
            {
                await agent.HardStopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }


    public async Task PrepareForRebuildsAsync()
    {
        if (_highWater.IsRunning)
        {
            await _highWater.StopAsync().ConfigureAwait(false);
        }

        await _highWater.CheckNowAsync().ConfigureAwait(false);
    }

    public async Task RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor = 0,
        DateTimeOffset? timestamp = null)
    {
        if (timestamp.HasValue)
        {
            sequenceFloor = await Database.FindEventStoreFloorAtTimeAsync(timestamp.Value, token).ConfigureAwait(false);
            if (sequenceFloor == null) return;
        }

        if (_cancellation.IsCancellationRequested) return;

        await stopRunningAgents(subscriptionName).ConfigureAwait(false);

        if (_cancellation.IsCancellationRequested) return;

        await _store.RewindSubscriptionProgressAsync(Database, subscriptionName, token, sequenceFloor).ConfigureAwait(false);

        var agents = buildAgentsForSubscription(subscriptionName);
        
        foreach (var agent in agents)
        {
            Tracker.MarkAsRestarted(agent.Name);
            var errorOptions = _store.RebuildErrors;
            await agent.StartAsync(new SubscriptionExecutionRequest(sequenceFloor!.Value, ShardExecutionMode.Continuous,
                errorOptions, this)).ConfigureAwait(false);
            agent.MarkHighWater(HighWaterMark());
        }
    }
    
    private IReadOnlyList<SubscriptionAgent> buildAgentsForSubscription(ISubscriptionSource<TOperations, TQuerySession> source)
    {
        var agents = new List<SubscriptionAgent>();

        foreach (var shard in source.Shards())
        {
            agents.Add(buildAgentForShard(shard));
        }

        return agents;
    }

    private IReadOnlyList<SubscriptionAgent> buildAgentsForSubscription(string subscriptionName)
    {
        var agents = new List<SubscriptionAgent>();

        foreach (var shard in _store.AllShards().Where(x => x.Name.Name.EqualsIgnoreCase(subscriptionName)))
        {
            agents.Add(buildAgentForShard(shard));
        }

        return agents;
    }
    
    public async Task CatchUpAsync(CancellationToken cancellation)
    {
        await StopAllAsync();
        await _highWater.CheckNowAsync();

        var progress = await Database.AllProjectionProgress(cancellation);

        foreach (var asyncShard in _store.AllShards())
        {
            var state = progress.FirstOrDefault(x => x.ShardName == asyncShard.Name.Identity)
                        ?? new ShardState(asyncShard.Name, 0);
            var agent = buildAgentForShard(asyncShard);
            _shardStateTracker = [];
            await agent.CatchUpAsync(HighWaterMark(), state, cancellation);
            var exceptions = _shardStateTracker.Select(x => x.Exception).Where(x => x != null).ToArray();
            if (exceptions.Length != 0)
            {
                throw new AggregateException(exceptions!);
            }
        }
    }
}

