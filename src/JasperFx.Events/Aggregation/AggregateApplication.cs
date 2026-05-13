using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level (all partials): aggregate Create/Apply/ShouldDelete method discovery scans TAggregate / TProjection via reflection, and the discovered handlers may call methods annotated RUC. The aggregate type T flows in from caller registration where trimming preserves T. AOT consumers must register concrete aggregate types per the AOT publishing guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): reflects PublicMethods on the aggregate / projection Type to discover Create/Apply/ShouldDelete handlers. Type flows in from caller-side generic parameters that trimming sees.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): assigns the result of reflective method/property lookup to DAM-annotated targets when building handler delegates via Expression.Call/CompileFast. Source type is preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): accesses PublicProperties on event-data Type returned by other reflection calls (e.g. GetGenericArguments). Event types are preserved by IEvent<T> registration on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2077:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): field/property of DAM-annotated type assigned from reflective lookups whose source type is preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): generic method parameter receives Type values obtained reflectively (e.g. eventType via IEvent.EventType / GetGenericArguments). Both source and target types are preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2090:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): generic class type argument flow at the aggregator instantiation point. TAggregate / TQuerySession are preserved by the registered projection boundary.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level (all partials): handler discovery uses Type.MakeGenericType / MethodInfo.MakeGenericMethod + FastExpressionCompiler.CompileFast — runtime code generation. AOT consumers should rely on the JasperFx.Events.SourceGenerator-emitted aggregator (covered by the AOT publishing guide).")]
internal partial class AggregateApplication<TAggregate, TQuerySession> : IAggregator<TAggregate, TQuerySession>, IMetadataApplication
{
    // This would be for external projections
    private readonly object? _projection;
    private readonly Type? _projectionType;
    
    private ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>> _applications = ImHashMap<Type, Func<TAggregate, IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>>.Empty;
    private ImHashMap<Type, Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>> _creators = ImHashMap<Type, Func<IEvent, TQuerySession, CancellationToken, ValueTask<TAggregate?>>>.Empty;
    private readonly CreateMethodCollection _createMethods;
    private readonly ApplyMethodCollection _applyMethods;
    private readonly ShouldDeleteMethodCollection _shouldDeleteMethods;
    private readonly IMetadataApplication? _metadataApplication;

    public AggregateApplication()
    {
        _projection = null;
        _projectionType = null;

        _createMethods = new CreateMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _applyMethods = new ApplyMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _shouldDeleteMethods = new ShouldDeleteMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
    }

    public AggregateApplication(object projection)
    {
        _projection = projection;
        _metadataApplication = projection as IMetadataApplication ?? this;
        _projectionType = projection.GetType();
        
        _createMethods = new CreateMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _applyMethods = new ApplyMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
        _shouldDeleteMethods = new ShouldDeleteMethodCollection(typeof(TQuerySession), _projectionType, typeof(TAggregate));
    }

    public Type IdentityType =>
        _projection is IAggregator<TAggregate, TQuerySession> agg ? agg.IdentityType : typeof(object);

    object IMetadataApplication.ApplyMetadata(object aggregate, IEvent lastEvent)
    {
        return aggregate;
    }

    public IEnumerable<Type> AllEventTypes()
    {
        return MethodCollection
            .AllEventTypes(_applyMethods, _createMethods, _shouldDeleteMethods)
            .Concat(_creators.Enumerate().Select(x => x.Key))
            .Concat(_applications.Enumerate().Select(x => x.Key))
            .Where(x => !_ignoredEventTypes.Contains(x))
            .Distinct().ToArray();
    }

    /// <summary>
    /// Really just for testing purposes
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="data"></param>
    /// <param name="session"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public ValueTask<TAggregate?> ApplyByDataAsync<T>(TAggregate snapshot, T data, TQuerySession session) where T : notnull
    {
        var e = new Event<T>(data);
        return ApplyAsync(snapshot, e, session, CancellationToken.None);
    }

    public ValueTask<TAggregate?> ApplyAsync(TAggregate snapshot, IEvent e, TQuerySession session, CancellationToken token)
    {
        if (_applications.TryFind(e.EventType, out var application))
        {
            return application(snapshot, e, session, token);
        }

        application = determineApplication(e.EventType);
        _applications = _applications.AddOrUpdate(e.EventType, application);

        return application(snapshot, e, session, token);
    }

    public bool HasAnyMethods()
    {
        return !_applyMethods.IsEmpty() || !_creators.IsEmpty || !_applications.IsEmpty;
    }

    public bool HasShouldDeleteMethods()
    {
        return _shouldDeleteMethods.Methods.Any();
    }
    
    public void AssertValidity()
    {
        if (_applyMethods.IsEmpty() && _createMethods.IsEmpty() && _creators.IsEmpty && _applications.IsEmpty)
        {
            throw new InvalidProjectionException(
                $"No matching conventional Apply/Create/ShouldDelete methods for the {typeof(TAggregate).FullNameInCode()} aggregate.");
        }

        if (_projectionType != null)
        {
            var invalidMethods =
                MethodCollection.FindInvalidMethods(_projectionType, _applyMethods, _createMethods, _shouldDeleteMethods)
                    .Where(x => !x.Method.HasAttribute<JasperFxIgnoreAttribute>()).ToArray();

            if (invalidMethods.Any())
            {
                throw new InvalidProjectionException(this, invalidMethods);
            }
        }
        else
        {
            var invalidMethods =
                MethodCollection.FindInvalidMethods(typeof(TAggregate), _applyMethods, _createMethods, _shouldDeleteMethods)
                    .Where(x => !x.Method.HasAttribute<JasperFxIgnoreAttribute>()).ToArray();

            if (invalidMethods.Any())
            {
                throw new InvalidProjectionException(this, invalidMethods);
            }
        }
        

    }

    public async ValueTask<TAggregate?> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TAggregate? snapshot, CancellationToken cancellation)
    {
        if (!events.Any()) return snapshot;
        
        foreach (var e in events)
        {
            if (snapshot == null)
            {
                snapshot = await Create(e, session, cancellation);
            }
            else
            {
                snapshot = await ApplyAsync(snapshot, e, session, cancellation);
            }
        }

        if (snapshot != null)
        {
            _metadataApplication!.ApplyMetadata(snapshot, events[^1]);
        }

        return snapshot;
    }
}