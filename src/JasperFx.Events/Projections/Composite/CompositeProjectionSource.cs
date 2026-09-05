using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.Composite;

/// <summary>
///     Presents a bare <see cref="IJasperFxProjection{TOperations}" /> as something a
///     <see cref="CompositeProjection{TOperations,TQuerySession}" /> stage can hold.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IJasperFxProjection{TOperations}" /> applies events and nothing else. A composite
///         stage holds an <see cref="IProjectionSource{TOperations,TQuerySession}" />, which also knows
///         its shards, its version and how to build an execution. This supplies all of that around the
///         projection. Lifted from the identical <c>CompositeIProjectionSource</c> types that lived in
///         Marten, Polecat and Fisher; the stores' versions are thin closings of this type over their
///         own session pairs.
///     </para>
///     <para>
///         <b>The execution deliberately does not dispose the batch it is handed.</b> Every stage of a
///         composite writes into one batch so the whole composite commits together — the composite owns
///         that lifecycle, and a stage disposing it would commit the earlier stages and leave the later
///         ones writing into a disposed session.
///     </para>
/// </remarks>
public class CompositeProjectionSource<TOperations, TQuerySession> :
    ProjectionBase,
    IProjectionSource<TOperations, TQuerySession>,
    ISubscriptionFactory<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IJasperFxProjection<TOperations> _projection;

    public CompositeProjectionSource(IJasperFxProjection<TOperations> projection)
    {
        _projection = projection;
        Lifecycle = ProjectionLifecycle.Async;
        Name = projection.GetType().Name;
        Version = 1;
        if (_projection.GetType().TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            Version = att.Version;
        }

        // polecat#439 / marten#5175 / fisher#63: adopt the wrapped projection's options and published
        // types when it has any, the way ProjectionWrapper and ScopedProjectionWrapper both do. Without
        // this the wrapper keeps the empty AsyncOptions it was constructed with, and a composite
        // rebuild -- whose teardown reads each member's PublishedTypes() and Options.CleanUps --
        // queues NOTHING for this member. The rebuild then restarts from sequence zero (its
        // progression row IS deleted) and replays onto the previous run's surviving rows, which is a
        // silent double-count.
        //
        // Name and Version are deliberately NOT adopted: they compose this member's
        // ShardName.Identity, and changing them would orphan every existing progression row.
        //
        // A raw projection that is not a ProjectionBase declares neither storage nor teardown, so
        // there is nothing to adopt and nothing this wrapper can invent. Declare it at registration
        // instead -- each store's composite exposes an Add(projection, Action<AsyncOptions>) overload
        // for exactly that.
        if (projection is ProjectionBase source)
        {
            replaceOptions(source.Options);

            foreach (var publishedType in source.PublishedTypes())
            {
                RegisterPublishedType(publishedType);
            }
        }
    }

    public SubscriptionType Type => SubscriptionType.EventProjection;
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];
    public Type ImplementationType => _projection.GetType();
    public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);

    public IReadOnlyList<AsyncShard<TOperations, TQuerySession>> Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection,
                new ShardName(Name, ShardName.All, Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        throw new NotSupportedException($"{GetType().NameInCode()} does not support inline execution");
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
    {
        return new CompositeProjectionSourceExecution<TOperations, TQuerySession>(_projection, shardName);
    }

    public ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, ILogger logger, ShardName shardName)
    {
        return new CompositeProjectionSourceExecution<TOperations, TQuerySession>(_projection, shardName);
    }
}

/// <summary>
///     One stage's execution for a bare <see cref="IJasperFxProjection{TOperations}" /> running inside a
///     composite: apply the range's events, per tenant, into the composite's shared batch.
/// </summary>
/// <remarks>
///     <b>The execution deliberately does not dispose the batch it is handed.</b> Every stage of a
///     composite writes into one batch so the whole composite commits together — the composite owns
///     that lifecycle, and a stage disposing it would commit the earlier stages and leave the later
///     ones writing into a disposed session.
/// </remarks>
public class CompositeProjectionSourceExecution<TOperations, TQuerySession> : ISubscriptionExecution
    where TOperations : TQuerySession, IStorageOperations
{
    private readonly IJasperFxProjection<TOperations> _projection;

    public CompositeProjectionSourceExecution(IJasperFxProjection<TOperations> projection, ShardName shardName)
    {
        _projection = projection;
        ShardName = shardName;
    }

    public ShardName ShardName { get; }
    public ShardExecutionMode Mode { get; set; }

    public async Task ProcessRangeAsync(EventRange range)
    {
        var batch = range.ActiveBatch as IProjectionBatch<TOperations, TQuerySession>;
        if (batch == null) return;

        var groups = range.Events.GroupBy(x => x.TenantId).ToArray();
        foreach (var group in groups)
        {
            await using var session = batch.SessionForTenant(group.Key);
            await _projection.ApplyAsync(session, group.ToList(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent) => new();
    public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;
    public Task HardStopAsync() => Task.CompletedTask;

    public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
        CancellationToken cancellation) => Task.CompletedTask;

    public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
    {
        caching = null;
        return false;
    }

    public ValueTask DisposeAsync() => new();
}
