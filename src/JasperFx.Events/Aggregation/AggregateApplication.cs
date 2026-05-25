using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Class-level: reflects PublicMethods on the aggregate / projection Type to discover Create/Apply/ShouldDelete handlers for validation. Type flows in from caller-side generic parameters that trimming sees.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level: assigns the result of reflective method lookup to DAM-annotated targets when building MethodCollection instances. Source type is preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
    Justification = "Class-level: accesses PublicProperties on event-data Type returned by other reflection calls (e.g. GetGenericArguments). Event types are preserved by IEvent<T> registration on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2077:DynamicallyAccessedMembers",
    Justification = "Class-level: field/property of DAM-annotated type assigned from reflective lookups whose source type is preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "Class-level: generic method parameter receives Type values obtained reflectively (e.g. eventType via IEvent.EventType / GetGenericArguments). Both source and target types are preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2090:DynamicallyAccessedMembers",
    Justification = "Class-level: generic class type argument flow at the aggregator instantiation point. TAggregate / TQuerySession are preserved by the registered projection boundary.")]
internal class AggregateApplication<TAggregate, TQuerySession> : IAggregator<TAggregate, TQuerySession>, IMetadataApplication
{
    private readonly object? _projection;
    private readonly Type? _projectionType;
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
            .Distinct().ToArray();
    }

    public bool HasAnyMethods()
    {
        return !_applyMethods.IsEmpty() || !_createMethods.IsEmpty();
    }

    public bool HasShouldDeleteMethods()
    {
        return _shouldDeleteMethods.Methods.Any();
    }

    /// <summary>
    /// Any conventional Apply/Create/ShouldDelete methods discovered on the aggregate
    /// or projection type via reflection. Used to decide whether a missing source-generated
    /// dispatcher is a fatal configuration error at registration time.
    /// </summary>
    public bool HasConventionalMethods()
    {
        return !_applyMethods.IsEmpty() || !_createMethods.IsEmpty() || _shouldDeleteMethods.Methods.Any();
    }

    public void AssertValidity()
    {
        if (_applyMethods.IsEmpty() && _createMethods.IsEmpty())
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

    internal string MissingDispatcherMessage()
    {
        var owner = _projectionType ?? typeof(TAggregate);
        return $"No source-generated dispatcher found for {owner.FullNameInCode()}. " +
               "Conventional Apply/Create/ShouldDelete methods are dispatched by the compile-time " +
               "JasperFx.Events.SourceGenerator; there is no runtime fallback. Ensure that analyzer runs in the " +
               $"assembly that defines {typeof(TAggregate).FullNameInCode()} (for Marten consumers the generator " +
               "ships inside the Marten NuGet package, so verify the project reference does not exclude the " +
               "'analyzers' asset). A self-aggregating type registered via Snapshot<T> / SingleStreamProjection<T> / " +
               "AggregateStream<T> does NOT need to be `partial`; a projection subclass that uses convention methods " +
               "DOES need to be declared `partial`. Alternatively, override " +
               "Evolve / EvolveAsync / DetermineAction / DetermineActionAsync directly.";
    }

    // IAggregator<>: the runtime aggregator contract. Reachable only when the registration-time
    // fail-fast in JasperFxAggregationProjectionBase did NOT fire (e.g. when AggregateApplication
    // is instantiated standalone outside the projection-base lifecycle). Throws so callers see
    // the same error message as the fail-fast.
    public ValueTask<TAggregate?> BuildAsync(IReadOnlyList<IEvent> events, TQuerySession session, TAggregate? snapshot,
        CancellationToken cancellation)
    {
        throw new InvalidOperationException(MissingDispatcherMessage());
    }
}
