using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Projections.ContainerScoped;

/// <summary>
/// Used to wrap IoC sourced IProjectionSource registrations that are not aggregations
/// </summary>
/// <typeparam name="TSource"></typeparam>
/// <typeparam name="TOperations"></typeparam>
/// <typeparam name="TQuerySession"></typeparam>
public class ScopedProjectionSourceWrapper<TSource, TOperations, TQuerySession> : ProjectionSourceWrapperBase<TSource, TOperations, TQuerySession>, IJasperFxProjection<TOperations>
    where TOperations : TQuerySession, IStorageOperations
    where TSource : IProjectionSource<TOperations, TQuerySession>, IJasperFxProjection<TOperations>
{
    public ScopedProjectionSourceWrapper(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    public override ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, storage, database, this, loggerFactory.CreateLogger<TSource>());
    }

    public override ISubscriptionExecution BuildExecution(IEventStorage<TOperations, TQuerySession> storage, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        return new ProjectionExecution<TOperations, TQuerySession>(shardName, Options, storage, database, this, logger);
    }

    async Task IJasperFxProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var source = (IJasperFxProjection<TOperations>)sp.GetRequiredService<TSource>();

        await source.ApplyAsync(operations, events, cancellation).ConfigureAwait(false);
    }
}