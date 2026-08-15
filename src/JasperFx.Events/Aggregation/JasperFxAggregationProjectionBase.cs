using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    /// <summary>
    ///     Explicitly register how the natural key of <typeparamref name="TDoc" /> is derived from events,
    ///     bypassing (or correcting) <c>[NaturalKeySource]</c> attribute discovery:
    ///     <code>
    ///     NaturalKeyFor(x => x
    ///         .SetBy&lt;ProductRegistered&gt;(e => new ProductCode(e.Code))
    ///         .SetByEvent&lt;ProductCodeChanged&gt;(e => new ProductCode(e.Data.NewCode)));
    ///     </code>
    ///     An explicit registration replaces whatever discovery found for the same event type, and clears
    ///     the configuration-time error a method that could not be bound would otherwise raise. The key
    ///     has to be a function of the event alone — the lookup table is maintained inline as events are
    ///     appended, where no prior aggregate exists. See jasperfx#569.
    /// </summary>
    public void NaturalKeyFor(Action<NaturalKeyBuilder<TDoc>> configure)
    {
        if (NaturalKeyDefinition == null)
        {
            throw new InvalidProjectionException(
                $"{typeof(TDoc).FullNameInCode()} has no property marked with [NaturalKey], so there is no natural key to configure.");
        }

        configure(new NaturalKeyBuilder<TDoc>(NaturalKeyDefinition));
    }

    /// <summary>
    ///     jasperfx#569: a [NaturalKeySource] method that discovery could not turn into a key extraction
    ///     used to be swallowed whole — no mapping, no log, no error — and the user found out when the
    ///     natural key lookup silently returned null for events of that type. Fail at configuration time
    ///     instead, naming the method and the reason.
    /// </summary>
    private void assertNaturalKeyValidity()
    {
        if (NaturalKeyDefinition == null || NaturalKeyDefinition.DiscoveryProblems.Count == 0) return;

        var problems = NaturalKeyDefinition.DiscoveryProblems
            .Select(x => "  * " + x)
            .Join(System.Environment.NewLine);

        throw new InvalidProjectionException(
            $"Unable to derive the natural key '{typeof(TDoc).FullNameInCode()}.{NaturalKeyDefinition.Member.Name}' from every [NaturalKeySource] method on {GetType().FullNameInCode()}:{System.Environment.NewLine}{problems}{System.Environment.NewLine}" +
            $"Either give the method a signature that derives the key from the event alone — a static method returning {NaturalKeyDefinition.OuterType.FullNameInCode()} that takes the event or IEvent<T> — or register the mapping explicitly with NaturalKeyFor(x => x.SetBy<TEvent>(...)).");
    }

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

    /// <summary>
    /// True when the projection genuinely overrides one of the dispatch virtuals, in which case that
    /// override owns event application and neither the generated evolver nor the conventional methods
    /// are consulted.
    ///
    /// <para>Declaring the method without <c>override</c> — the compiler's CS0114 case, whether or not
    /// the author added <c>new</c> — hides the base virtual instead of replacing it. It used to count
    /// here, purely because it is declared outside JasperFx.Events, and the consequences were entirely
    /// silent: with conventional methods alongside it, registration failed claiming the projection
    /// "can only use the override of 'Evolve' or conventional Apply/Create/ShouldDelete methods, but
    /// not both" — a conflict the author never wrote — and without them, dispatch reached the base
    /// virtual and threw <c>NotImplementedException("Did you forget to implement this?")</c> at the
    /// first event. <see cref="MethodInfo.GetBaseDefinition"/> separates the two: an override reports
    /// the base-class declaration, a hiding member reports itself. See #656.</para>
    /// </summary>
    private bool isOverridden(string methodName)
    {
        var method = findDispatchMethod(methodName);

        return method != null
               && method.DeclaringType!.Assembly != typeof(IEvent).Assembly
               && method.GetBaseDefinition().DeclaringType!.Assembly == typeof(IEvent).Assembly;
    }

    private bool isSourceGeneratedOverride(string methodName)
    {
        var method = findDispatchMethod(methodName);
        return method != null
               && method.DeclaringType!.Assembly != typeof(IEvent).Assembly
               && method.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), false);
    }

    /// <summary>
    /// The dispatch virtual named <paramref name="methodName"/> as this projection sees it.
    /// <c>Type.GetMethod(string)</c> throws <see cref="AmbiguousMatchException"/> as soon as the
    /// projection declares any other method of the same name — a private <c>Evolve(string)</c> helper
    /// was enough to break registration — so match on the base declaration's signature instead.
    /// </summary>
    private MethodInfo? findDispatchMethod(string methodName)
    {
        var baseDeclaration = typeof(JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>)
            .GetMethods()
            .FirstOrDefault(m => m.Name == methodName);

        if (baseDeclaration == null) return null;

        var parameterTypes = baseDeclaration.GetParameters().Select(p => p.ParameterType).ToArray();

        return GetType().GetMethods()
            .FirstOrDefault(m => m.Name == methodName
                                 && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes));
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
                var evolver = (IGeneratedSyncEvolver<TDoc, TId>)activateEvolver(evolverType);
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
                var evolver = (IGeneratedSyncDetermineAction<TDoc, TId>)activateEvolver(evolverType);
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
                var evolver = (IGeneratedAsyncDetermineAction<TDoc, TId>)activateEvolver(evolverType);
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
                var evolver = (IGeneratedAsyncEvolver<TDoc, TId>)activateEvolver(evolverType);
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
    /// Builds a generated evolver, handing it this projection when it asks for one.
    ///
    /// <para>An evolver that dispatches to conventional methods living on the projection instance —
    /// rather than on the aggregate or a static — needs an instance to call them on. It used to build
    /// its own: <c>new TProjection()</c>, or <c>RuntimeHelpers.GetUninitializedObject</c> when there was
    /// no public parameterless constructor. Both are shadows of the projection the store actually
    /// registered, so anything the real constructor or the container supplied was missing — null for an
    /// injected dependency (marten#4787), default for anything set in the constructor.</para>
    ///
    /// <para>The generator therefore emits a constructor taking the projection type on any evolver that
    /// needs an instance, and we pass <c>this</c>. Evolvers that need nothing from the projection
    /// (aggregate-side or static conventional methods) keep their parameterless constructor, as do
    /// evolvers generated before this change, so the fallback below is the compatible path rather than
    /// an error case. See #653.</para>
    /// </summary>
    private object activateEvolver(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        Type evolverType)
    {
        var boundConstructor = evolverType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1
                                 && c.GetParameters()[0].ParameterType.IsInstanceOfType(this));

        if (boundConstructor != null)
        {
            return boundConstructor.Invoke([this]);
        }

        return Activator.CreateInstance(evolverType)!;
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
        // Ahead of everything else, and outside the source-generated short circuit below: natural key
        // discovery is independent of how the aggregation itself is dispatched.
        assertNaturalKeyValidity();

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

    private static readonly PropertyInfo _eventDataProperty =
        typeof(IEvent).GetProperty(nameof(IEvent.Data))!;

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

            var eventType = determineEventType(parameters, docType);
            if (eventType == null)
            {
                definition.RecordProblem(method, method.DeclaringType ?? searchType,
                    "no event parameter could be identified — a [NaturalKeySource] method has to accept the event, either as the event type itself or as IEvent<T>");
                continue;
            }

            // Skip if we already have a mapping for this event type
            if (definition.HasMappingFor(eventType)) continue;

            try
            {
                var extractor = buildExtractor(method, naturalKeyProp, docType, parameters, eventType,
                    out var reason);
                if (extractor != null)
                {
                    definition.AddOrReplaceMapping(eventType, extractor);
                }
                else
                {
                    definition.RecordProblem(method, eventType, reason!);
                }
            }
            catch (Exception e)
            {
                // jasperfx#569: this used to be a bare `catch { }`, so a method that could not be bound
                // produced no mapping, no log, and no configuration-time error — the natural key lookup
                // table was simply never written for that event type, and the user found out when
                // FetchLatest returned null. Keep the failure, and make validation report it.
                definition.RecordProblem(method, eventType,
                    $"building a key extraction failed with {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Which event does this [NaturalKeySource] method handle? The event can arrive as the event type
    /// itself or as IEvent&lt;T&gt;, and it is not necessarily the first parameter — an evolve method
    /// may take the prior aggregate first.
    /// </summary>
    private static Type? determineEventType(ParameterInfo[] parameters, Type docType)
    {
        foreach (var parameter in parameters)
        {
            var parameterType = parameter.ParameterType;

            if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IEvent<>))
            {
                return parameterType.GetGenericArguments()[0];
            }

            // The prior aggregate, and a bare IEvent, both say nothing about which event this handles.
            if (parameterType == docType || parameterType == typeof(IEvent)) continue;

            return parameterType;
        }

        return null;
    }

    /// <summary>
    /// Compile the "derive this event's natural key value" function for one [NaturalKeySource] method.
    /// The extractor receives the whole <see cref="IEvent" /> (jasperfx#569), which is what makes an
    /// IEvent&lt;T&gt; handler bindable at all. Preference order, most trustworthy first:
    ///   1. a static method that IS a key extraction — it returns the natural key type and is a pure
    ///      function of the event, so nothing has to be fabricated;
    ///   2. a property of the natural key type carried directly on the event body;
    ///   3. invoking the user's aggregation method against a fabricated blank aggregate.
    /// (3) is the legacy path and stays last for a reason: it runs user code against an aggregate that
    /// was never built by any constructor the user wrote, so a handler body that touches any state other
    /// than the key throws — out of the extractor, out of the inline natural key maintenance, and out of
    /// the caller's SaveChangesAsync. It is now gated on the aggregate being safely constructible. Note
    /// that whatever path is used, the key is derived from the event alone: Marten maintains the lookup
    /// table inline at append time, where no prior aggregate exists under an Async snapshot lifecycle, so
    /// a key that depends on prior aggregate state is not expressible here.
    /// </summary>
    private static Func<IEvent, object?>? buildExtractor(
        MethodInfo method,
        PropertyInfo naturalKeyProp,
        Type docType,
        ParameterInfo[] parameters,
        Type eventType,
        out string? reason)
    {
        reason = null;

        var keyType = naturalKeyProp.PropertyType;
        var eventParam = Expression.Parameter(typeof(IEvent), "e");
        var blockers = new List<string>();

        // 1. The dedicated key extraction signature: static, returns the natural key type, and every
        //    parameter comes off the event. Nothing is fabricated and no user aggregation code runs.
        //      [NaturalKeySource] public static Code KeyFor(CodeChanged e) => new Code(e.NewCode);
        //      [NaturalKeySource] public static Code KeyFor(IEvent<CodeChanged> e) => ...;
        if (method.IsStatic && method.ReturnType == keyType)
        {
            if (tryBindArguments(eventParam, parameters, eventType, docType, null, out var keyArgs,
                    out var blocker))
            {
                return compile(Expression.Convert(Expression.Call(method, keyArgs!), typeof(object)),
                    eventParam);
            }

            blockers.Add(blocker!);
        }

        // 2. The natural key value carried directly on the event body.
        var fromEventBody = tryReadKeyOffTheEvent(eventParam, eventType, naturalKeyProp, out var matchBlocker);
        if (fromEventBody != null)
        {
            return compile(fromEventBody, eventParam);
        }

        if (matchBlocker != null) blockers.Add(matchBlocker);

        // 3. Last resort: run the user's aggregation method against a fabricated aggregate.
        var fabricated = tryInvokeAgainstFabricatedAggregate(method, naturalKeyProp, docType, parameters,
            eventType, eventParam, out var fabricationBlocker);
        if (fabricated != null)
        {
            return compile(fabricated, eventParam);
        }

        if (fabricationBlocker != null) blockers.Add(fabricationBlocker);

        reason = blockers.Any()
            ? blockers.Join("; ")
            : $"no way to derive a {keyType.FullNameInCode()} from a {eventType.FullNameInCode()} could be determined from this signature";

        return null;
    }

    /// <summary>
    /// Bind one argument per parameter out of the <see cref="IEvent" /> the extractor is handed.
    /// <paramref name="fabricatedAggregate" /> is non-null only on the fabricated-aggregate path; a
    /// TDoc parameter is otherwise unbindable, which is exactly what keeps path (1) honest.
    /// </summary>
    private static bool tryBindArguments(
        ParameterExpression eventParam,
        ParameterInfo[] parameters,
        Type eventType,
        Type docType,
        Expression? fabricatedAggregate,
        out Expression[]? arguments,
        out string? blocker)
    {
        var args = new Expression[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;

            if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IEvent<>))
            {
                args[i] = Expression.Convert(eventParam, parameterType);
            }
            else if (parameterType == typeof(IEvent))
            {
                args[i] = eventParam;
            }
            else if (parameterType == docType)
            {
                if (fabricatedAggregate == null)
                {
                    arguments = null;
                    blocker =
                        $"parameter '{parameters[i].Name}' is the prior {docType.FullNameInCode()}, which is not available when the natural key is derived from the event alone";
                    return false;
                }

                args[i] = fabricatedAggregate;
            }
            else if (parameterType.IsAssignableFrom(eventType))
            {
                args[i] = Expression.Convert(Expression.Property(eventParam, _eventDataProperty), parameterType);
            }
            else
            {
                arguments = null;
                blocker =
                    $"parameter '{parameters[i].Name}' of type {parameterType.FullNameInCode()} cannot be supplied from the event";
                return false;
            }
        }

        arguments = args;
        blocker = null;
        return true;
    }

    /// <summary>
    /// Read the natural key straight off the event body when the event carries a property of the key's
    /// type. A single candidate is unambiguous; several are only usable when one of them shares the name
    /// of the natural key property, because silently taking the first declared one is how an event that
    /// carries both an old and a new key gets the wrong answer.
    /// </summary>
    private static Expression? tryReadKeyOffTheEvent(
        ParameterExpression eventParam,
        Type eventType,
        PropertyInfo naturalKeyProp,
        out string? blocker)
    {
        blocker = null;

        var candidates = eventType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == naturalKeyProp.PropertyType && p.GetMethod != null)
            .ToArray();

        if (candidates.Length == 0) return null;

        var chosen = candidates.Length == 1
            ? candidates[0]
            : candidates.FirstOrDefault(p =>
                string.Equals(p.Name, naturalKeyProp.Name, StringComparison.OrdinalIgnoreCase));

        if (chosen == null)
        {
            blocker =
                $"{eventType.FullNameInCode()} carries more than one {naturalKeyProp.PropertyType.FullNameInCode()} property ({candidates.Select(x => x.Name).Join(", ")}) and none of them is named '{naturalKeyProp.Name}', so which one is the natural key is ambiguous";
            return null;
        }

        return Expression.Convert(
            Expression.Property(
                Expression.Convert(Expression.Property(eventParam, _eventDataProperty), eventType),
                chosen),
            typeof(object));
    }

    /// <summary>
    /// The legacy path: build a blank TDoc, run the user's own Create/Apply method against it, and read
    /// the natural key back off the result. Covers the self-aggregating create factory (marten#4277) and
    /// the static evolve method that changes the key (marten#4966). Gated on TDoc being safely
    /// constructible, since <c>Expression.New</c> bypasses required-member enforcement and hands the
    /// user's method an aggregate that no constructor of theirs ever produced (marten#5041).
    /// </summary>
    private static Expression? tryInvokeAgainstFabricatedAggregate(
        MethodInfo method,
        PropertyInfo naturalKeyProp,
        Type docType,
        ParameterInfo[] parameters,
        Type eventType,
        ParameterExpression eventParam,
        out string? blocker)
    {
        var needsAggregate = !method.IsStatic || parameters.Any(x => x.ParameterType == docType);

        if (needsAggregate && !canSafelyFabricate(docType, out blocker))
        {
            return null;
        }

        if (!method.IsStatic && method.DeclaringType != docType)
        {
            blocker =
                $"it is an instance method on {method.DeclaringType?.FullNameInCode()}, which discovery has no instance of — make it static, or register the mapping explicitly with NaturalKeyFor()";
            return null;
        }

        var docVariable = Expression.Variable(docType, "doc");

        if (!tryBindArguments(eventParam, parameters, eventType, docType, docVariable, out var args,
                out blocker))
        {
            return null;
        }

        var call = method.IsStatic
            ? Expression.Call(method, args!)
            : Expression.Call(docVariable, method, args!);

        var statements = new List<Expression>();
        if (needsAggregate)
        {
            statements.Add(Expression.Assign(docVariable, Expression.New(docType)));
        }

        Expression keyExpression;
        if (method.ReturnType == naturalKeyProp.PropertyType)
        {
            // e.g. an instance method on the aggregate that simply returns the key
            keyExpression = call;
        }
        else if (method.ReturnType == docType)
        {
            // The evolve/create shape — read the key off what the method produced, not off the blank.
            keyExpression = Expression.Property(call, naturalKeyProp);
        }
        else if (!method.IsStatic)
        {
            // The classic mutating instance Apply — call it, then read the key it set on the aggregate.
            statements.Add(call);
            keyExpression = Expression.Property(docVariable, naturalKeyProp);
        }
        else
        {
            blocker =
                $"it is static and returns {method.ReturnType.FullNameInCode()}, which is neither the aggregate nor the natural key type {naturalKeyProp.PropertyType.FullNameInCode()}, so there is nothing to read the key from";
            return null;
        }

        statements.Add(Expression.Convert(keyExpression, typeof(object)));

        blocker = null;
        return Expression.Block([docVariable], statements);
    }

    private static bool canSafelyFabricate(Type docType, out string? blocker)
    {
        var constructor = docType.GetConstructor(System.Type.EmptyTypes);
        if (constructor == null)
        {
            blocker =
                $"deriving the key means calling this method against a blank {docType.FullNameInCode()}, which has no public parameterless constructor";
            return false;
        }

        // Expression.New happily ignores `required`, so the user's method would run against an aggregate
        // with null members that C# itself would never have let them create. That is the marten#5041
        // ArgumentNullException, thrown out of a plain event append.
        var hasRequiredMembers =
            docType.GetCustomAttributes(inherit: true).Any(x => x is RequiredMemberAttribute);
        var constructorSetsThem =
            constructor.GetCustomAttributes(inherit: true).Any(x => x is SetsRequiredMembersAttribute);

        if (hasRequiredMembers && !constructorSetsThem)
        {
            blocker =
                $"deriving the key means calling this method against a blank {docType.FullNameInCode()}, but it declares required members that a parameterless constructor cannot satisfy";
            return false;
        }

        blocker = null;
        return true;
    }

    private static Func<IEvent, object?> compile(Expression body, ParameterExpression eventParam)
        => Expression.Lambda<Func<IEvent, object?>>(body, eventParam).Compile();
}