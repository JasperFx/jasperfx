using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections;

/// <summary>
/// Base class for adhoc projections, and the shared implementation behind each store's own
/// <c>EventProjection</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>Derive from your store's <c>EventProjection</c>, not from this type.</b> Same divergence, and
/// same silence, as <see cref="Aggregation.JasperFxSingleStreamProjectionBase{TDoc,TId,TOperations,TQuerySession}" />
/// — see that type's remarks and <see href="https://github.com/JasperFx/jasperfx/issues/649" />. As of
/// Marten 9.23, <c>Marten.Events.Projections.EventProjection</c> adds <c>IProjectionSchemaSource</c>, the
/// store's validation hook, and a <c>TryBuildReplayExecutor</c> override that this base does not have;
/// taking the base instead compiles cleanly and quietly loses them.
/// </remarks>
/// <typeparam name="TOperations"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
    Justification = "Class-level: GetType().GetMethod(...) for self-introspection of overridden methods. The concrete projection subclass is preserved by its registration on the caller side.")]
public abstract class JasperFxEventProjectionBase<TOperations, TQuerySession> :
    ProjectionBase,
    IProjectionSource<TOperations, TQuerySession>,
    ISubscriptionFactory<TOperations, TQuerySession>,
    IInlineProjection<TOperations>,
    IEntityStorage<TOperations>,
    IJasperFxProjection<TOperations>,
    IEventEnrichment<TQuerySession> where TOperations : TQuerySession, IStorageOperations
{
    private readonly EventProjectionApplication<TOperations> _application;
    public Type ProjectionType => GetType();

    public JasperFxEventProjectionBase()
    {
        _application = new EventProjectionApplication<TOperations>(this);
        
        IncludedEventTypes.Fill(_application.AllEventTypes());

        foreach (var publishedType in _application.PublishedTypes())
        {
            RegisterPublishedType(publishedType);
        }

        Name = GetType().FullNameInCode();
    }

    public SubscriptionType Type => SubscriptionType.EventProjection;
    public ShardName[] ShardNames() => [ShardName.Compose(Name, version: Version)];
    public Type ImplementationType => GetType();

    public virtual SubscriptionDescriptor Describe(IEventStore store)
    {
        return new SubscriptionDescriptor(this, store);
    }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, ShardName.Compose(Name, version: Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        return this;
    }

    async Task IInlineProjection<TOperations>.ApplyAsync(TOperations operations, IEnumerable<StreamAction> streams, CancellationToken cancellation)
    {
        var events = streams.SelectMany(x => x.Events).ToList();
        await EnrichEventsAsync(operations, events, cancellation);
        await applyAsync(operations, events, cancellation);
    }

    /// <summary>
    /// Override this to enrich events with additional data before they are applied.
    /// This is called once per tenant batch before individual event processing begins.
    /// Use this to batch-load reference data and avoid N+1 query problems.
    /// </summary>
    /// <param name="querySession">A query session for the current tenant</param>
    /// <param name="events">All events in the current batch</param>
    /// <param name="cancellation"></param>
    public virtual Task EnrichEventsAsync(TQuerySession querySession, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Override this for explicit projection logic
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="e"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public virtual ValueTask ApplyAsync(TOperations operations, IEvent e, CancellationToken cancellation)
    {
        return _application.ApplyAsync(operations, e, cancellation);
    }

    async Task IJasperFxProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        await applyAsync(operations, events, cancellation);
    }

    private async Task applyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        foreach (var e in events)
        {
            try
            {
                await ApplyAsync(operations, e, cancellation);
            }
            catch (Exception ex)
            {
                if (ProjectionExceptions.IsExceptionTransient(ex))
                {
                    throw;  
                }
                else
                {
                    throw new ApplyEventException(e, ex);
                }
            }
        }
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, store, database, this, logger);
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, store, database, this, logger);
    }

    void IEntityStorage<TOperations>.Store<T>(TOperations ops, T entity) 
    {
        storeEntity<T>(ops, entity);
    }

    protected abstract void storeEntity<T>(TOperations ops, T entity) where T : notnull;

    /// <summary>
    /// jasperfx#626: whether the document types this projection publishes are automatically
    /// registered as teardown targets (<see cref="AsyncOptions.DeleteViewTypeOnTeardown(Type)" />),
    /// so a rebuild wipes the previous run's documents before re-projecting. True by default, which
    /// matches what aggregation projections have always done for their single view type.
    ///
    /// <para>
    /// Set to false when this projection writes into storage that must NOT be truncated on rebuild —
    /// an append-only audit table, or documents another projection owns. With it off, nothing is
    /// registered automatically and <see cref="ProjectionBase.Options" />'s teardown rules are
    /// entirely yours to declare. It is all-or-nothing: to keep automatic registration for some
    /// published types only, turn it off and call <c>Options.DeleteViewTypeOnTeardown&lt;T&gt;()</c>
    /// for the ones you do want wiped.
    /// </para>
    /// </summary>
    public bool DeletePublishedTypesOnTeardown { get; set; } = true;

    // jasperfx#626: an EventProjection can publish several document types, so the base constructor
    // cannot do what JasperFxAggregationProjectionBase does with its single TDoc -- and it could not
    // do it there anyway, because the source generator emits its RegisterPublishedType calls into the
    // subclass constructor, which runs AFTER this base one. Deferring to AssembleAndAssertValidity
    // (registration time, via ProjectionGraph) sees the complete set and lets a subclass constructor
    // turn the behavior off before it happens. Idempotent: a type the author already registered by
    // hand, or a previous pass, is skipped rather than duplicated.
    private void registerPublishedTypesForTeardown()
    {
        if (!DeletePublishedTypesOnTeardown) return;

        foreach (var publishedType in PublishedTypes().ToArray())
        {
            if (Options.StorageTypes.Contains(publishedType)) continue;
            Options.DeleteViewTypeOnTeardown(publishedType);
        }
    }

    public sealed override void AssembleAndAssertValidity()
    {
        registerPublishedTypesForTeardown();

        var applyMethod = GetType()!.GetMethod(nameof(ApplyAsync))!;
        var isOverridden = applyMethod.DeclaringType!.Assembly != typeof(JasperFxEventProjectionBase<,>).Assembly;
        if (isOverridden)
        {
            var isSourceGenerated = applyMethod.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), false);
            if (!isSourceGenerated && _application.HasAnyMethods())
            {
                throw new InvalidProjectionException(
                    "Event projections can be written by either overriding the ApplyAsync() method or by using conventional methods, but not both");
            }
        }
        else
        {
            _application.AssertMethodValidity();

            // AssertMethodValidity passed, so conventional Project/Create/Transform methods exist.
            // ApplyAsync was not overridden (neither by the user nor by the source generator) —
            // fail fast at registration with a clear message rather than blowing up at first dispatch.
            throw new InvalidProjectionException(_application.MissingDispatcherMessage());
        }
    }
}