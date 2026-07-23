using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Aggregation;

[UnconditionalSuppressMessage("Trimming", "IL2065:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): reflects on `this.GetType()` / projection type for handler discovery. The concrete projection type is preserved by registration on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): parameter receiving DAM-annotated Type from reflective lookup. Both source and target preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): reflects PublicMethods / PublicProperties on TDoc / projection Type for aggregation step discovery. Types preserved at registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): assigns reflective Type/MethodInfo results to DAM-annotated targets. Source types preserved at registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): PublicProperties access via Type returned by other reflection calls. Source preserved at registration.")]
[UnconditionalSuppressMessage("Trimming", "IL2090:DynamicallyAccessedMembers",
    Justification = "Class-level (all partials): generic type argument flow at base-class instantiation. TDoc/TId/TOperations/TQuerySession preserved by registration.")]
public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    : ProjectionBase, IAggregateProjection, IAggregationSteps<TDoc, TQuerySession>,
        IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession>,
        IAggregationProjection<TDoc, TId, TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    private readonly Lazy<Type[]> _allEventTypes;
    private readonly AggregateApplication<TDoc, TQuerySession> _application;
    
    private readonly AggregateVersioning<TDoc, TQuerySession> _versioning;
    private Type[]? _generatedEvolverEventTypes;
    private bool _usesConventionalApplication = true;

    protected JasperFxAggregationProjectionBase(AggregationScope scope)
    {
        Scope = scope;
        Name = typeof(TDoc).NameInCode();

        Type = scope == AggregationScope.SingleStream
            ? SubscriptionType.SingleStreamProjection
            : SubscriptionType.MultiStreamProjection;

        // We'll use this to validate even if it's not used at runtime
        _application = new AggregateApplication<TDoc, TQuerySession>(this);
        
        _buildAction = buildActionAsync;
        
        establishBuildActionAndEvolve();
        
        Options.DeleteViewTypeOnTeardown<TDoc>();

        _allEventTypes = new Lazy<Type[]>(determineEventTypes);

        _versioning = new AggregateVersioning<TDoc, TQuerySession>(scope) { Inner = _application };

        RegisterPublishedType(typeof(TDoc));

        if (typeof(TDoc).TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            base.Version = att.Version;
        }

        NaturalKeyDefinition = discoverNaturalKey(GetType());
    }

    public NaturalKeyDefinition? NaturalKeyDefinition { get; }

    public Type ImplementationType => GetType();
    public SubscriptionType Type { get; }
    public ShardName[] ShardNames() => [ShardName.Compose(Name, version: Version)];

    private static readonly string[] methodNames = [nameof(DetermineAction), nameof(DetermineActionAsync), nameof(Evolve), nameof(EvolveAsync)];
    
    [MemberNotNull(nameof(_evolve))]
    private void establishBuildActionAndEvolve()
    {
        if (isOverridden(nameof(DetermineAction)))
        {
            _usesConventionalApplication = false;
            _buildAction = (_, snapshot, id, _, events, _) => new ValueTask<(TDoc?, ActionType)>(DetermineAction(snapshot, id, events));
        }
        else if (isOverridden(nameof(DetermineActionAsync)))
        {
            _usesConventionalApplication = false;
            _buildAction = DetermineActionAsync;
        }
        else if (isOverridden(nameof(Evolve)))
        {
            _usesConventionalApplication = false;
            _evolve = evolveDefault;
        }
        else if (isOverridden(nameof(EvolveAsync)))
        {
            _usesConventionalApplication = false;
            _evolve = evolveDefaultAsync;
        }
        else if (tryUseAssemblyRegisteredEvolver())
        {
            // Source-generated evolver found for self-aggregating type
            _usesConventionalApplication = false;
        }
        else
        {
            _usesConventionalApplication = true;
            _evolve = evolveDefaultAsync;
        }
    }

    private bool isOverridden(string methodName)
    {
        return GetType().GetMethod(methodName)!.DeclaringType!.Assembly != typeof(IEvent).Assembly;
    }

    private bool isSourceGeneratedOverride(string methodName)
    {
        var method = GetType().GetMethod(methodName);
        return method != null
               && method.DeclaringType!.Assembly != typeof(IEvent).Assembly
               && method.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), false);
    }

    [MemberNotNullWhen(true, nameof(_evolve))]
    private bool tryUseAssemblyRegisteredEvolver()
    {
        var hasShouldDelete = _application.HasShouldDeleteMethods();
        var docType = typeof(TDoc);

        // Scan both the aggregate's assembly AND the projection's own assembly for
        // GeneratedEvolverAttribute registrations. For a self-aggregating type these are the
        // same assembly. For a partial projection whose aggregate lives in a different assembly
        // than the projection subclass (the common domain-library + composition-root split), the
        // generator emits the registration into the *projection's* assembly, so the aggregate's
        // assembly alone is not enough. The file-scoped evolver the generator now emits replaced
        // the old "inject an override into the user's class" approach, which travelled with the
        // projection instance regardless of assembly; scanning the projection assembly preserves
        // that reach. See https://github.com/JasperFx/jasperfx/issues/462.
        // Select the single best-matching registration before dispatching. An evolver emitted for a
        // specific projection subclass (ProjectionType set) must bind ONLY to that projection (or a
        // subclass of it) — several projections can target the same aggregate with different dispatch
        // logic, and a no-op projection sharing the aggregate must NOT borrow another projection's evolver
        // (that would skip validation and mis-dispatch). Priority: an exact projection match wins; then a
        // BASE-class projection match (a derived projection that only customizes Name/Options inherits its
        // base's convention methods, hence its generated evolver); then a self-aggregating (ProjectionType
        // null) registration is the fallback. See #462.
        GeneratedEvolverAttribute? exactMatch = null;
        GeneratedEvolverAttribute? baseMatch = null;
        GeneratedEvolverAttribute? aggregateOnly = null;
        foreach (var attr in collectGeneratedEvolverAttributes(docType))
        {
            if (attr.AggregateType != docType) continue;

            if (attr.ProjectionType != null)
            {
                if (attr.ProjectionType == GetType())
                {
                    exactMatch = attr;
                    break;
                }

                // A derived projection class (e.g. a subclass that only customizes Name/Options — the
                // common "custom projection name" pattern) inherits the convention methods, and therefore
                // the generated evolver, of its base projection. Accept an evolver whose ProjectionType is
                // a base class of this projection, preferring the most-derived such base. Sibling
                // projections are not assignable to one another, so this never lets unrelated projections
                // borrow each other's dispatch logic.
                if (attr.ProjectionType.IsAssignableFrom(GetType())
                    && evolverImplementsIdentityContract(attr.EvolverType)
                    && (baseMatch == null || baseMatch.ProjectionType!.IsAssignableFrom(attr.ProjectionType)))
                {
                    baseMatch = attr;
                }

                continue;
            }

            // Aggregate-only (self-aggregating) evolver: acceptable fallback, but ONLY when its evolver
            // implements a <TDoc, TId> generated contract for THIS identity type. When the same aggregate
            // is registered against multiple identity types (#297 — e.g. AggregateStream<CountOfLetters>
            // with both Guid and string ids) the generator emits one evolver per TId, all keyed on the
            // aggregate type with a null ProjectionType. Selecting purely by aggregate type could pick the
            // wrong-TId evolver, whose strongly-typed interface checks below would all fail, leaving _evolve
            // unwired and tripping the "no source-generated dispatcher" backstop.
            if (evolverImplementsIdentityContract(attr.EvolverType))
            {
                aggregateOnly ??= attr;
            }
        }

        var selected = exactMatch ?? baseMatch ?? aggregateOnly;

        if (selected != null)
        {
            var evolverType = selected.EvolverType;

            // (selection above already guaranteed this for aggregate-only matches; a
            // projection-specific match is bound to a single TId by construction.)

            // Check for IGeneratedSyncEvolver<TDoc, TId>. Skip this branch when
            // the projection has ShouldDelete methods — a plain SyncEvolver
            // only knows about Apply/Create on the aggregate type itself, so
            // the ShouldDelete contract is unreachable from it. The SG knows
            // to emit IGeneratedSyncDetermineAction for ShouldDelete-having
            // projections, which the next branch picks up. See #297.
            var syncEvolverInterface = typeof(IGeneratedSyncEvolver<TDoc, TId>);
            if (!hasShouldDelete && syncEvolverInterface.IsAssignableFrom(evolverType))
            {
                var evolver = (IGeneratedSyncEvolver<TDoc, TId>)Activator.CreateInstance(evolverType)!;
                _generatedEvolverEventTypes = evolver.EventTypes;
                _evolve = (snapshot, id, _, events, _) =>
                {
                    foreach (var e in events)
                    {
                        try
                        {
                            snapshot = evolver.Evolve(snapshot, id, e);
                        }
                        catch (Exception ex)
                        {
                            // Transient errors bubble for Polly to retry; non-transient
                            // user errors get wrapped in ApplyEventException so the
                            // daemon's SkipApplyErrors handler can route just the
                            // offending event to the dead-letter queue. Matches the
                            // semantics of the pre-#276 reflection path in
                            // evolveDefaultAsync. See #303.
                            if (ProjectionExceptions.IsExceptionTransient(ex)) throw;
                            throw new ApplyEventException(e, ex);
                        }
                    }

                    return new ValueTask<TDoc?>(snapshot);
                };
                return true;
            }

            // Check for IGeneratedSyncDetermineAction<TDoc, TId> — handles ShouldDelete natively
            var determineActionInterface = typeof(IGeneratedSyncDetermineAction<TDoc, TId>);
            if (determineActionInterface.IsAssignableFrom(evolverType))
            {
                var evolver = (IGeneratedSyncDetermineAction<TDoc, TId>)Activator.CreateInstance(evolverType)!;
                _generatedEvolverEventTypes = evolver.EventTypes;
                _buildAction = (_, snapshot, id, _, events, _) =>
                {
                    // Dispatch one event at a time so a poison-pill Apply can be wrapped
                    // in ApplyEventException carrying *that* event. Bulk-dispatching the
                    // whole batch through DetermineAction would lose the per-event seam
                    // the daemon's SkipApplyErrors handler relies on (see #303). The
                    // final action is whichever the last event produced — same outcome
                    // as a single batch call because DetermineAction's per-event branches
                    // are independent of the rest of the batch state apart from snapshot.
                    var action = ActionType.Nothing;
                    var single = new IEvent[1];
                    foreach (var e in events)
                    {
                        single[0] = e;
                        try
                        {
                            (snapshot, action) = evolver.DetermineAction(snapshot, id, single);
                        }
                        catch (ApplyEventException)
                        {
                            // Already wrapped (either by the SG-emitted dispatcher
                            // per #305 or by user code throwing it explicitly); pass
                            // through unchanged so we don't double-wrap.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (ProjectionExceptions.IsExceptionTransient(ex)) throw;
                            throw new ApplyEventException(e, ex);
                        }
                    }

                    return new ValueTask<(TDoc?, ActionType)>((snapshot, action));
                };
                _evolve = evolveDefaultAsync; // not used when _buildAction is set, but must be non-null
                return true;
            }

            // Check for IGeneratedAsyncDetermineAction<TDoc, TId> — ShouldDelete projections whose
            // Apply/Create/ShouldDelete handlers are async and/or need an IQuerySession. The generated
            // DetermineActionAsync iterates the whole slice and already wraps each failing event in an
            // ApplyEventException (preserving the per-event seam the daemon's SkipApplyErrors handler
            // relies on), so the runtime calls it once with the full list. See #462.
            var asyncDetermineActionInterface = typeof(IGeneratedAsyncDetermineAction<TDoc, TId>);
            if (asyncDetermineActionInterface.IsAssignableFrom(evolverType))
            {
                var evolver = (IGeneratedAsyncDetermineAction<TDoc, TId>)Activator.CreateInstance(evolverType)!;
                _generatedEvolverEventTypes = evolver.EventTypes;
                _buildAction = (session, snapshot, id, _, events, ct) =>
                    evolver.DetermineActionAsync(snapshot, id, events, session!, ct);
                _evolve = evolveDefaultAsync; // not used when _buildAction is set, but must be non-null
                return true;
            }

            // Check for IGeneratedAsyncEvolver<TDoc, TId> — Evolve/EvolveAsync on self-aggregating types, no ShouldDelete arm
            var asyncEvolverInterface = typeof(IGeneratedAsyncEvolver<TDoc, TId>);
            if (!hasShouldDelete && asyncEvolverInterface.IsAssignableFrom(evolverType))
            {
                var evolver = (IGeneratedAsyncEvolver<TDoc, TId>)Activator.CreateInstance(evolverType)!;
                _generatedEvolverEventTypes = evolver.EventTypes;
                _evolve = async (snapshot, id, session, events, ct) =>
                {
                    foreach (var e in events)
                    {
                        try
                        {
                            snapshot = await evolver.EvolveAsync(snapshot, id, e, session!, ct);
                        }
                        catch (ApplyEventException)
                        {
                            // Already wrapped (either by the SG-emitted dispatcher
                            // per #305 or by user code throwing it explicitly); pass
                            // through unchanged so we don't double-wrap.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (ProjectionExceptions.IsExceptionTransient(ex)) throw;
                            throw new ApplyEventException(e, ex);
                        }
                    }

                    return snapshot;
                };
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="evolverType"/> implements one of the generated <c>&lt;TDoc, TId&gt;</c>
    /// dispatcher contracts for THIS projection's identity type. Used to disambiguate the self-aggregating
    /// fallback when one aggregate is registered against multiple identity types (#297) — only the evolver
    /// emitted for the matching TId can actually be wired below.
    /// </summary>
    private static bool evolverImplementsIdentityContract(Type evolverType)
    {
        return typeof(IGeneratedSyncEvolver<TDoc, TId>).IsAssignableFrom(evolverType)
               || typeof(IGeneratedSyncDetermineAction<TDoc, TId>).IsAssignableFrom(evolverType)
               || typeof(IGeneratedAsyncDetermineAction<TDoc, TId>).IsAssignableFrom(evolverType)
               || typeof(IGeneratedAsyncEvolver<TDoc, TId>).IsAssignableFrom(evolverType);
    }

    /// <summary>
    /// Gathers <see cref="GeneratedEvolverAttribute"/> registrations from the aggregate's assembly and,
    /// when different, the concrete projection's own assembly. A partial projection whose aggregate is
    /// declared in another assembly has its generated evolver registered alongside the projection, not
    /// the aggregate, so both must be consulted. See #462.
    /// </summary>
    private IEnumerable<GeneratedEvolverAttribute> collectGeneratedEvolverAttributes(Type docType)
    {
        foreach (var attr in docType.Assembly.GetCustomAttributes<GeneratedEvolverAttribute>())
        {
            yield return attr;
        }

        var projectionAssembly = GetType().Assembly;
        if (projectionAssembly != docType.Assembly)
        {
            foreach (var attr in projectionAssembly.GetCustomAttributes<GeneratedEvolverAttribute>())
            {
                yield return attr;
            }
        }
    }


    protected bool IsUsingConventionalMethods => _usesConventionalApplication;
    
    public override void AssembleAndAssertValidity()
    {
        // If a source-generated evolver was found (either for Apply/Create or Evolve/EvolveAsync),
        // skip conventional method validation — the evolver handles everything
        if (_generatedEvolverEventTypes != null)
        {
            var types = determineEventTypes();
            IncludedEventTypes.Fill(types);
            return;
        }

        var overrides = methodNames.Where(isOverridden).ToArray();
        var sgOverrides = overrides.Where(isSourceGeneratedOverride).ToArray();
        var userOverrides = overrides.Except(sgOverrides).ToArray();

        if (sgOverrides.Length > 0)
        {
            // The source generator emitted the dispatcher into the partial projection class.
            // Conventional Apply/Create/ShouldDelete methods are what it dispatches to — that
            // pairing is intentional, not a configuration conflict. A simultaneous user
            // override of one of the same methods would create two competing dispatch paths,
            // so block that combination.
            if (userOverrides.Length > 0)
            {
                throw new InvalidProjectionException(
                    $"Source generator emitted '{sgOverrides[0]}' for {GetType().FullNameInCode()}; " +
                    $"cannot also manually override '{userOverrides[0]}' on the same projection.");
            }
        }
        else
        {
            switch (userOverrides.Length)
            {
                case 0:
                    _application.AssertValidity();

                    // AssertValidity passed, so conventional Apply/Create methods exist on this
                    // projection or its aggregate. Neither a user override nor a source-generated
                    // dispatcher is in place to consume them — fail fast at registration with a
                    // clear message rather than blowing up at first event dispatch.
                    throw new InvalidProjectionException(_application.MissingDispatcherMessage());
                case 1:
                    if (_application.HasAnyMethods())
                    {
                        throw new InvalidProjectionException(
                            $"This projection can only use the override of '{userOverrides[0]}' or conventional Apply/Create/ShouldDelete methods, but not both");
                    }

                    break;
                case 2:
                    throw new InvalidProjectionException("Only one of these methods can be overridden: " +
                                                        userOverrides.Join(", "));
            }
        }

        var eventTypes = determineEventTypes();
        IncludedEventTypes.Fill(eventTypes);
    }

    internal IList<Type> DeleteEvents { get; } = new List<Type>();
    internal IList<Type> TransformedEvents { get; } = new List<Type>();
    public AggregationScope Scope { get; }

    public Type AggregateType => typeof(TDoc);
    public Type IdentityType => typeof(TId);

    public Type[] AllEventTypes => _allEventTypes.Value;

    /// <summary>
    ///     Template method that is called on the last event in a slice of events that
    ///     are updating an aggregate. This was added specifically to add metadata like "LastModifiedBy"
    ///     from the last event to an aggregate with user-defined logic. Override this for your own specific logic
    /// </summary>
    /// <param name="snapshot"></param>
    /// <param name="lastEvent"></param>
    public virtual TDoc ApplyMetadata(TDoc snapshot, IEvent lastEvent)
    {
        return snapshot;
    }

    public bool MatchesAnyDeleteType(IReadOnlyList<IEvent> events)
    {
        return events.Select(x => x.EventType).Intersect(DeleteEvents).Any();
    }

    /// <summary>
    /// Potentially raise "side effects" during projection processing to either emit additional events,
    /// or publish messages
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    public virtual ValueTask RaiseSideEffects(TOperations operations, IEventSlice<TDoc> slice)
    {
        return new ValueTask();
    }

    /// <summary>
    /// Potentially raise "side effects" during projection processing to either emit additional events,
    /// or publish messages. The identity of the current slice is supplied as <paramref name="id"/>.
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="id"></param>
    /// <param name="slice"></param>
    /// <returns></returns>
    public virtual ValueTask RaiseSideEffects(TOperations operations, TId id, IEventSlice<TDoc> slice)
    {
        return RaiseSideEffects(operations, slice);
    }

    public SubscriptionDescriptor Describe(IEventStore store)
    {
        return new SubscriptionDescriptor(this, store);
    }

    public Type ProjectionType => GetType();

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, ShardName.Compose(Name, version: Version), this, this)
        ];
    }

    public virtual bool TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)]out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    /// <summary>
    /// Single/multi-stream aggregations fan cleanly into a composite single-pass rebuild by default.
    /// Custom-grouped multi-stream projections override this to opt out (jasperfx#407 Phase A).
    /// </summary>
    public virtual bool CanParticipateInCompositeReplay => true;

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline() => buildForInline();

    protected abstract IInlineProjection<TOperations> buildForInline();

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(
        IEventStore<TOperations, TQuerySession> store,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());

        var session = store.OpenSession(database);
        var slicer = BuildSlicer(session);

        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(store, database, this,
                SliceBehavior.Preprocess, slicer, logger);

        return new GroupedProjectionExecution(shardName, runner, logger){Disposables = [session]};
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(
        IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger,
        ShardName shardName)
    {
        var session = store.OpenSession(database);
        var slicer = BuildSlicer(session);

        var runner =
            new AggregationRunner<TDoc, TId, TOperations, TQuerySession>(store, database, this,
                SliceBehavior.Preprocess, slicer, logger);

        return new GroupedProjectionExecution(shardName, runner, logger){Disposables = [session]};
    }

    protected virtual Type[] determineEventTypes()
    {
        var types = _application.AllEventTypes()
            .Concat(DeleteEvents).Concat(TransformedEvents).Concat(IncludedEventTypes);

        if (_generatedEvolverEventTypes != null)
        {
            types = types.Concat(_generatedEvolverEventTypes);
        }

        return types.Distinct().ToArray();
    }

    public bool AppliesTo(IEnumerable<Type> eventTypes)
    {
        // Have to do this because you don't know if any events catch
        if (AllEventTypes.Length == 0) return true;
        
        return eventTypes
            .Intersect(AllEventTypes).Any() || eventTypes.Any(type => AllEventTypes.Any(type.CanBeCastTo));
    }

    /// <summary>
    ///     When used as an asynchronous projection, this opts into
    ///     only taking in events from streams explicitly marked as being
    ///     the aggregate type for this projection. Only use this if you are explicitly
    ///     marking streams with the aggregate type on StartStream()
    /// </summary>
    [JasperFxIgnore]
    public void FilterIncomingEventsOnStreamType()
    {
        StreamType = typeof(TDoc);
    }

    public abstract IEventSlicer BuildSlicer(TQuerySession session);

    void IAggregationProjection<TDoc, TId, TOperations, TQuerySession>.StartBatch()
    {
        // Nothing, this is a hook for something else
    }

    ValueTask IAggregationProjection<TDoc, TId, TOperations, TQuerySession>.EndBatchAsync()
    {
        return new ValueTask();
    }

    /// <summary>
    /// Hook that you can override in order to do "event enrichment" where you might batch up
    /// database lookups to add information to events prior to applying them in a projection
    /// running asynchronously. Note that this method is called *after* slicing, but before applying
    /// events
    /// </summary>
    /// <param name="group"></param>
    /// <param name="querySession"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    public virtual Task EnrichEventsAsync(SliceGroup<TDoc, TId> group, TQuerySession querySession,
        CancellationToken cancellation)
    {
        return Task.CompletedTask;
    }

    (IEvent?, TDoc?) IAggregationProjection<TDoc, TId, TOperations, TQuerySession>.TryApplyMetadata(
        IReadOnlyList<IEvent> events, TDoc? aggregate, TId id, IIdentitySetter<TDoc, TId> identitySetter)
    {
        return tryApplyMetadata(events, aggregate, id, identitySetter);
    }
    
    protected (IEvent?, TDoc?) tryApplyMetadata(
        IReadOnlyList<IEvent> events, 
        TDoc? aggregate,
        TId id,
        IIdentitySetter<TDoc, TId> storage)
    {
        var lastEvent = events.LastOrDefault();
        if (aggregate != null)
        {
            foreach (var @event in events)
            {
                aggregate = ApplyMetadata(aggregate, @event);
            }
            
            storage.SetIdentity(aggregate, id);
            _versioning.TrySetVersion(aggregate, lastEvent);
        }

        if (lastEvent != null && aggregate is IHasTenantId tenanted)
        {
            tenanted.TenantId = lastEvent.TenantId;
        }

        return (lastEvent, aggregate);
    }

    private static NaturalKeyDefinition? discoverNaturalKey(Type projectionType)
    {
        var docType = typeof(TDoc);

        // Find a property marked with [NaturalKey]
        var naturalKeyProp = docType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetCustomAttribute<NaturalKeyAttribute>() != null);

        if (naturalKeyProp == null) return null;

        var definition = new NaturalKeyDefinition(docType, naturalKeyProp);

        // Discover [NaturalKeySource] methods on the aggregate type. Include BOTH instance
        // methods (the classic Apply(TEvent) pattern on the aggregate) AND static methods
        // (self-aggregating records/classes that expose a static factory such as
        //   public static TDoc Create(TEvent e) => new TDoc(...);
        // as in https://github.com/JasperFx/marten/issues/4277).
        discoverNaturalKeySourceMethods(definition, naturalKeyProp, docType,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        // Also discover [NaturalKeySource] methods on a separate projection class when
        // the projection is not the aggregate itself.
        if (projectionType != docType)
        {
            discoverNaturalKeySourceMethods(definition, naturalKeyProp, projectionType,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        }

        return definition;
    }

    private static void discoverNaturalKeySourceMethods(
        NaturalKeyDefinition definition,
        PropertyInfo naturalKeyProp,
        Type searchType,
        BindingFlags bindingFlags)
    {
        var docType = typeof(TDoc);
        var methods = searchType.GetMethods(bindingFlags)
            .Where(m => m.GetCustomAttribute<NaturalKeySourceAttribute>() != null);

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0) continue;

            // Determine the event type from the first parameter.
            // It can be the raw event type or IEvent<T>.
            var firstParamType = parameters[0].ParameterType;
            Type eventType;
            if (firstParamType.IsGenericType &&
                firstParamType.GetGenericTypeDefinition() == typeof(IEvent<>))
            {
                eventType = firstParamType.GetGenericArguments()[0];
            }
            else if (typeof(IEvent).IsAssignableFrom(firstParamType))
            {
                eventType = firstParamType;
            }
            else
            {
                eventType = firstParamType;
            }

            // Skip if we already have a mapping for this event type
            if (definition.EventMappings.Any(m => m.EventType == eventType))
                continue;

            try
            {
                var extractor = buildExtractor(method, naturalKeyProp, docType, parameters);
                if (extractor != null)
                {
                    definition.EventMappings.Add(new NaturalKeyEventMapping(eventType, extractor));
                }
            }
            catch
            {
                // Silently skip methods we can't build extractors for
            }
        }
    }

    private static Func<object, object?>? buildExtractor(
        MethodInfo method,
        PropertyInfo naturalKeyProp,
        Type docType,
        ParameterInfo[] parameters)
    {
        var eventParam = Expression.Parameter(typeof(object), "e");
        var firstParamType = parameters[0].ParameterType;

        // For instance methods on the aggregate (the original working pattern):
        // Create a new TDoc, call the method, read the natural key property
        if (!method.IsStatic && method.DeclaringType == docType)
        {
            var eventType = firstParamType;
            var docParam = Expression.Variable(docType, "doc");

            var body = Expression.Block(
                [docParam],
                Expression.Assign(docParam, Expression.New(docType)),
                Expression.Call(docParam, method, Expression.Convert(eventParam, eventType)),
                Expression.Convert(Expression.Property(docParam, naturalKeyProp), typeof(object))
            );

            return Expression.Lambda<Func<object, object?>>(body, eventParam).Compile();
        }

        // For static methods on the aggregate itself that return a TDoc, call the method
        // and read the natural key property off the returned aggregate. This covers BOTH:
        //   * the self-aggregating create factory (JasperFx/marten#4277):
        //       public static TDoc Create(TEvent e) => new TDoc(...);
        //   * an evolve/update method that CHANGES the natural key (JasperFx/marten#4966):
        //       public static TDoc Apply(TEvent e, TDoc current) => current with { Key = ... };
        // Build one argument per parameter: the event parameter receives the raw event data
        // (converted); a TDoc parameter (the prior aggregate in an evolve method) receives a
        // fresh default aggregate, mirroring the instance-method branch above. Only the event
        // data reaches the extractor (NaturalKeyProjection passes @event.Data), so a parameter
        // that needs IEvent<T> metadata — or a doc type without a public parameterless ctor —
        // can't be satisfied here; in that case fall through to the property-matching fallback.
        if (method.IsStatic && method.DeclaringType == docType && method.ReturnType == docType)
        {
            var callArgs = new Expression[parameters.Length];
            var eventArgBound = false;
            var canCall = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                var isIEvent = paramType.IsGenericType
                    && paramType.GetGenericTypeDefinition() == typeof(IEvent<>);

                if (paramType == docType && docType.GetConstructor(System.Type.EmptyTypes) != null)
                {
                    callArgs[i] = Expression.New(docType);
                }
                else if (!eventArgBound && paramType != docType && !isIEvent)
                {
                    callArgs[i] = Expression.Convert(eventParam, paramType);
                    eventArgBound = true;
                }
                else
                {
                    canCall = false;
                    break;
                }
            }

            if (canCall && eventArgBound)
            {
                var body = Expression.Convert(
                    Expression.Property(
                        Expression.Call(method, callArgs),
                        naturalKeyProp),
                    typeof(object));

                return Expression.Lambda<Func<object, object?>>(body, eventParam).Compile();
            }
        }

        // For static methods on the projection class, we can't safely call them
        // (they may need IEvent with StreamKey, etc.). Instead, find a matching
        // property on the event data type and read it directly.
        Type eventDataType;
        if (firstParamType.IsGenericType && firstParamType.GetGenericTypeDefinition() == typeof(IEvent<>))
        {
            eventDataType = firstParamType.GetGenericArguments()[0];
        }
        else if (firstParamType == docType && parameters.Length >= 2)
        {
            var secondType = parameters[1].ParameterType;
            eventDataType = secondType.IsGenericType && secondType.GetGenericTypeDefinition() == typeof(IEvent<>)
                ? secondType.GetGenericArguments()[0]
                : secondType;
        }
        else
        {
            eventDataType = firstParamType;
        }

        // Search for a property on the event data that matches the natural key by type
        var eventKeyProp = eventDataType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.PropertyType == naturalKeyProp.PropertyType);

        if (eventKeyProp == null) return null;

        var body2 = Expression.Convert(
            Expression.Property(
                Expression.Convert(eventParam, eventDataType),
                eventKeyProp),
            typeof(object));

        return Expression.Lambda<Func<object, object?>>(body2, eventParam).Compile();
    }
}