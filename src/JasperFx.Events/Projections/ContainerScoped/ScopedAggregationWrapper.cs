using JasperFx.Core.Reflection;
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
public class ScopedAggregationWrapper<TSource, TDoc, TId, TOperations, TQuerySession> :
    ProjectionSourceWrapperBase<TSource, TOperations, TQuerySession>, IJasperFxProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
    where TSource : JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
{
    public ScopedAggregationWrapper(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        Scope = typeof(TSource).Closes(typeof(JasperFxSingleStreamProjectionBase<,,,>))
            ? AggregationScope.SingleStream
            : AggregationScope.MultiStream;
    }

    public AggregationScope Scope { get; }

    public override ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        return BuildExecution(storage, database, logger, shardName);
    }

    public override ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        // TODO -- may need to track the disposable of the session here
        var session = storage.OpenSession(database);
        var slicer = new EventSlicer(session, _serviceProvider);
        
        var runner = new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(storage, database, new ScopedAggregationProjection<TSource,TDoc,TId,TOperations,TQuerySession>(_serviceProvider, this),
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
    }
}