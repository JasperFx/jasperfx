using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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

    /// <summary>
    /// jasperfx#887: this used to have to describe every cause at once, because it could not tell
    /// "the generator never ran in that assembly" — a csproj problem — from "the generator ran and
    /// declined your type" — a code-shape problem. The marker the generator now leaves in every
    /// assembly it processes decides between them, and the assemblies asked are the ones the runtime
    /// actually scans for a dispatcher.
    /// </summary>
    internal string MissingDispatcherMessage()
    {
        var owner = _projectionType ?? typeof(TAggregate);

        return $"No source-generated dispatcher found for {owner.FullNameInCode()}. " +
               "Conventional Apply/Create/ShouldDelete methods are dispatched by the compile-time " +
               "JasperFx.Events.SourceGenerator; there is no runtime fallback. " +
               SourceGeneratorMarker.DescribeGeneratorReach(
                   EvidenceAssembliesFor(typeof(TAggregate), _projectionType),
                   $"the aggregate {typeof(TAggregate).FullNameInCode()}");
    }

    /// <summary>
    /// The assemblies whose marker is genuine evidence about whether the generator reached
    /// <paramref name="aggregateType" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted and made internal for jasperfx#906, which was a inverted verdict rather than a wording
    /// problem: a projection registered as <c>Snapshot&lt;T&gt;()</c>,
    /// <c>SingleStreamProjection&lt;T, TId&gt;</c> or <c>AggregateStream&lt;T&gt;</c> has a
    /// <em>framework-owned</em> projection type — <c>Marten.Events.Aggregation.SingleStreamProjection&lt;T,
    /// TId&gt;</c>, whose assembly is <c>Marten.dll</c>. The store's own build runs the generator, so that
    /// assembly ALWAYS carries the marker. Admitting it to the evidence list pinned the verdict to "the
    /// generator ran" for the most common registration shape in the product, and the message then told
    /// the reader that the cause they actually had was not the cause. marten#5495 is exactly that shape.
    /// </para>
    /// <para>
    /// The discriminator is <see cref="Type.IsConstructedGenericType" />, and it is narrower than it may
    /// look. It is deliberately NOT a change of quantifier from <c>Any</c> to <c>All</c>: a user-declared
    /// <c>MyProjection : SingleStreamProjection&lt;Agg, Guid&gt;</c> in assembly B really does get its
    /// projection-specific evolver emitted into B, so confirmation in B is genuine evidence even when the
    /// aggregate's assembly carries no marker, and <c>All</c> would break that. Such a subclass is not a
    /// constructed generic, so it is still admitted.
    /// </para>
    /// <para>
    /// A <em>closed generic</em> is excluded whoever owns it, framework or user. There is no user
    /// declaration for the generator to hang a projection-specific evolver on — the closing happens at
    /// run time — so nothing can have been emitted for it, in any assembly, and its assembly's marker
    /// says nothing about this aggregate. The same-assembly case needs no special handling: if the
    /// projection type lives in the aggregate's assembly it is already in the list.
    /// </para>
    /// </remarks>
    internal static List<Assembly> EvidenceAssembliesFor(Type aggregateType, Type? projectionType)
    {
        var assemblies = new List<Assembly> { aggregateType.Assembly };

        if (projectionType == null || projectionType.Assembly == aggregateType.Assembly)
        {
            return assemblies;
        }

        if (projectionType.IsConstructedGenericType)
        {
            return assemblies;
        }

        // A declared projection type of its own: the generator emits a projection-specific evolver into
        // ITS assembly, so confirmation there is just as good as confirmation in the aggregate's — see
        // collectGeneratedEvolverAttributes, which scans both.
        assemblies.Add(projectionType.Assembly);

        return assemblies;
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
