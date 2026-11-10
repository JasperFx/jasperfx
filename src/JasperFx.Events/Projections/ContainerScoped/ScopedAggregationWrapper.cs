using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.ContainerScoped;

/// <summary>
/// IoC scoped wrapper for aggregation projections
/// </summary>
/// <typeparam name="TSource"></typeparam>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
/// <typeparam name="TOperations"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument flow on the IoC wrapper. TSource/TDoc/TId/TOperations/TQuerySession are preserved by the IoC registration of the projection on the caller side.")]
public class ScopedAggregationWrapper<TSource, TDoc, TId, TOperations, TQuerySession> :
    ProjectionSourceWrapperBase<TSource, TOperations, TQuerySession>, IJasperFxProjection<TOperations>,
    IAggregateProjection, ISteppableAggregation<TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
    where TSource : JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    where TDoc : notnull
    where TId : notnull
{
    public ScopedAggregationWrapper(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        Scope = typeof(TSource).Closes(typeof(JasperFxSingleStreamProjectionBase<,,,>))
            ? AggregationScope.SingleStream
            : AggregationScope.MultiStream;
    }

    public AggregationScope Scope { get; }

    public override ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return BuildExecution(store, database, logger, shardName);
    }

    public override ISubscriptionExecution BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        // TODO -- may need to track the disposable of the session here
        var session = store.OpenSession(database);
        var slicer = new EventSlicer(session, _serviceProvider);
        
        var runner = new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(store, database, new ScopedAggregationProjection<TSource,TDoc,TId,TOperations,TQuerySession>(_serviceProvider, this),
            SliceBehavior.Preprocess, slicer, logger);

        return new GroupedProjectionExecution(shardName, runner, logger);
    }

    public async Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var projection = (IJasperFxProjection<TOperations>)sp.GetRequiredService<TSource>();
        await projection.ApplyAsync(operations, events, cancellation);
    }

    // jasperfx#1049: a Scoped or Transient projection reaches the graph as this wrapper, so without these
    // a step-through refused it as "not an aggregation", while the stores' AggregatorFor<T> path replayed it.
    async Task<MultiAggregateProjectionResult> ISteppableAggregation<TQuerySession>.BuildTimelinesAsync(
        IReadOnlyList<IEvent> events, TQuerySession session, Func<object?, JsonElement?> serialize,
        Func<IEvent, EventRecord> toRecord, IProjectionStepObserver? observer, CancellationToken cancellation)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<TSource>()
            .BuildTimelinesAsync(events, session, serialize, toRecord, observer, cancellation).ConfigureAwait(false);
    }

    async Task<MultiAggregateProjectionResult> ISteppableAggregation<TQuerySession>.BuildTimelinesAsync(
        IReadOnlyList<IEvent> events, TQuerySession session, ProjectionStateSerializer states,
        Func<IEvent, EventRecord> toRecord, IProjectionStepObserver? observer, CancellationToken cancellation)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<TSource>()
            .BuildTimelinesAsync(events, session, states, toRecord, observer, cancellation).ConfigureAwait(false);
    }

    async Task<ProjectionTimeline<JsonElement?>> ISteppableAggregation<TQuerySession>.BuildTimelineAsync(
        object identity, IReadOnlyList<EventRecord> events, Func<EventRecord, IEvent?> toEvent,
        TQuerySession session, JsonElement? startingState, ProjectionStateSerializer states,
        CancellationToken cancellation)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<TSource>()
            .BuildTimelineAsync(identity, events, toEvent, session, startingState, states, cancellation)
            .ConfigureAwait(false);
    }

    internal class EventSlicer : IEventSlicer
    {
        private readonly TQuerySession _session;
        private readonly IServiceProvider _services;

        public EventSlicer(TQuerySession session, IServiceProvider services)
        {
            _session = session;
            _services = services;
        }

        public async ValueTask<IReadOnlyList<object>> SliceAsync(IReadOnlyList<IEvent> events)
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;
            var source = sp.GetRequiredService<TSource>();
            var slicer = source.BuildSlicer(_session);
            return await slicer.SliceAsync(events);
        }

        public async ValueTask<IReadOnlyList<object>> SliceAsync(EventRange range)
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;
            var source = sp.GetRequiredService<TSource>();
            var slicer = source.BuildSlicer(_session);
            return await slicer.SliceAsync(range);
        }
    }

    public Type IdentityType => typeof(TId);
    public Type AggregateType => typeof(TDoc);
    public Type[] AllEventTypes => IncludedEventTypes.ToArray();
    public NaturalKeyDefinition? NaturalKeyDefinition => null;
}