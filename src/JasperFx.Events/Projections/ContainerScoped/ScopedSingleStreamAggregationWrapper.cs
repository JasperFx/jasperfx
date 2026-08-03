using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.Projections.ContainerScoped;

/// <summary>
/// Non-generic helper for building the right container-scoped aggregation wrapper for a
/// projection type. Single stream projections get a wrapper that can also serve native
/// live aggregation; everything else gets the plain wrapper
/// </summary>
public static class ScopedAggregationWrapper
{
    /// <summary>
    /// Build the container-scoped wrapper appropriate to <paramref name="sourceType" />. Event
    /// stores should call this rather than closing a wrapper type directly, so that single
    /// stream projections registered with a Scoped or Transient lifetime stay usable from
    /// live aggregation and single stream rebuilds. See marten#5095
    /// </summary>
    public static ProjectionBase Build(IServiceProvider services, Type sourceType, Type documentType,
        Type identityType, Type operationsType, Type querySessionType)
    {
        var openType = sourceType.Closes(typeof(JasperFxSingleStreamProjectionBase<,,,>))
            ? typeof(ScopedSingleStreamAggregationWrapper<,,,,>)
            : typeof(ScopedAggregationWrapper<,,,,>);

        try
        {
            return openType.CloseAndBuildAs<ProjectionBase>(services, sourceType, documentType, identityType,
                operationsType, querySessionType);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            // The wrapper validates the projection it wraps from its constructor, and the
            // constructor runs through reflection -- so a projection configuration error would
            // otherwise reach the user as a TargetInvocationException instead of the actual
            // InvalidProjectionException a Singleton registration reports. Rethrow the real one
            // with its original stack trace intact.
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// IoC scoped wrapper for single stream aggregation projections. Adds
/// <see cref="IAggregatorSource{TQuerySession}" /> on top of the general aggregation wrapper so
/// that <c>AggregatorFor</c> resolves the real projection for native live aggregation and
/// single stream rebuilds instead of silently falling through to conventional aggregation
/// built off the aggregate type. Only single stream projections implement
/// <see cref="IAggregator{T,TSession}" />, which is why this is a separate type rather than
/// something the base wrapper does for every aggregation projection
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic type-argument flow on the IoC wrapper. TSource/TDoc/TId/TOperations/TQuerySession are preserved by the IoC registration of the projection on the caller side.")]
public class ScopedSingleStreamAggregationWrapper<TSource, TDoc, TId, TOperations, TQuerySession> :
    ScopedAggregationWrapper<TSource, TDoc, TId, TOperations, TQuerySession>, IAggregatorSource<TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
    where TSource : JasperFxSingleStreamProjectionBase<TDoc, TId, TOperations, TQuerySession>
    where TDoc : notnull
    where TId : notnull
{
    public ScopedSingleStreamAggregationWrapper(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    Type IAggregatorSource<TQuerySession>.AggregateType => typeof(TDoc);

    IAggregator<T, TQuerySession> IAggregatorSource<TQuerySession>.Build<T>()
    {
        return new ScopedAggregator<TSource, TDoc, TId, TOperations, TQuerySession>(_serviceProvider)
            .As<IAggregator<T, TQuerySession>>();
    }

    IAggregator<T, TIdentity, TQuerySession> IAggregatorSource<TQuerySession>.Build<T, TIdentity>()
    {
        return new ScopedAggregator<TSource, TDoc, TId, TOperations, TQuerySession>(_serviceProvider)
            .As<IAggregator<T, TIdentity, TQuerySession>>();
    }
}

/// <summary>
/// Aggregator that resolves the projection from a fresh container scope on every aggregation
/// call. Deliberately holds no projection instance: the whole point of a Scoped or Transient
/// registration is that the projection and its dependency graph are not safe to cache, and
/// <c>AggregatorFor</c> caches the aggregator itself for the life of the store
/// </summary>
internal class ScopedAggregator<TSource, TDoc, TId, TOperations, TQuerySession> :
    IAggregator<TDoc, TQuerySession>, IAggregator<TDoc, TId, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
    where TSource : JasperFxSingleStreamProjectionBase<TDoc, TId, TOperations, TQuerySession>
    where TDoc : notnull
    where TId : notnull
{
    private readonly IServiceProvider _serviceProvider;

    public ScopedAggregator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Type IdentityType => typeof(TId);

    public async ValueTask<TDoc?> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot,
        CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<TSource>();

        return await source.As<IAggregator<TDoc, TQuerySession>>()
            .BuildAsync(events, session, snapshot, cancellation).ConfigureAwait(false);
    }

    public async ValueTask<TDoc?> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TDoc? snapshot,
        TId id, IIdentitySetter<TDoc, TId> identitySetter, CancellationToken cancellation)
    {
        using var scope = _serviceProvider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<TSource>();

        return await source.As<IAggregator<TDoc, TId, TQuerySession>>()
            .BuildAsync(events, session, snapshot, id, identitySetter, cancellation).ConfigureAwait(false);
    }
}
