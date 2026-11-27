using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Per-slice builder. Names, groups, annotates and links a slice — and, since jasperfx#957, declares its
/// roles, by CLR type or by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaring roles is design-first work.</b> A role declared here sits on the
/// <see cref="EventModelProvenance.Declared"/> rung: once Wolverine (or another source) derives the same
/// role from code, the derived claim wins, and any difference becomes a
/// <see cref="HotspotOrigin.SourceDisagreement"/> hotspot — the gap between the model and the code, which
/// is the design-first to-do list. jasperfx#703's ladder is what makes this safe; before it, a declaration
/// could overwrite what the code does, which is why jasperfx#687 had cut this builder back to an overlay.
/// </para>
/// <para>
/// <b>Types or names.</b> Every role takes a CLR type (<c>Emits&lt;AppointmentConfirmed&gt;()</c>) or a
/// name (<c>Emits("AppointmentConfirmed")</c>). A name is recorded as a declaration — no namespace, no
/// assembly — and matches the real type by name once it exists (jasperfx#798, jasperfx#958). Prefer a
/// stub type as soon as you have one: a typo in a name is a silent second element, a typo in a type is a
/// compile error.
/// </para>
/// </remarks>
public class EventModelSliceBuilder
{
    private readonly string _sliceName;
    private string? _domain;
    private string? _chapter;
    private string? _triggerLabel;
    private readonly List<SpecificationDescriptor> _specifications = new();
    private readonly List<HotspotDescriptor> _hotspots = new();
    private ExternallyOwnedRoles? _externallyOwned;

    private SlicePattern? _pattern;
    private TriggerKind? _triggerKind;
    private TypeDescriptor? _triggerType;
    private TypeDescriptor? _commandType;
    private TypeDescriptor? _handlerType;
    private TypeDescriptor? _startsStream;
    private bool _noAggregate;
    private TypeDescriptor? _deciderModel;
    private ViewProjection? _viewProjection;
    private readonly List<TypeDescriptor> _aggregateTypes = new();
    private readonly List<TypeDescriptor> _emittedEvents = new();
    private readonly List<EmittedEventStream> _emittedEventStreams = new();
    private TypeDescriptor? _lastEmitted;
    private readonly List<TypeDescriptor> _publishedMessages = new();
    private readonly List<TypeDescriptor> _consumedEvents = new();
    private readonly List<TypeDescriptor> _readsFrom = new();
    private readonly List<TypeDescriptor> _readModelTypes = new();
    private readonly List<TypeDescriptor> _projectionTypes = new();
    private readonly List<ExternalSystemDescriptor> _externalSystems = new();

    // jasperfx#1297
    private readonly List<EmittedEventTags> _eventTags = new();
    private readonly List<BoundaryReadDescriptor> _reads = new();
    private readonly List<TypeDescriptor> _untaggedProjections = new();
    private readonly List<string> _rejectionReasons = new();
    private (BoundaryReadKind kind, TypeDescriptor type)? _lastBoundaryRead;

    // jasperfx#1298
    private string? _actorName;
    private readonly List<string> _requiredRoles = new();
    private TypeDescriptor? _lastConsumed;
    private readonly List<ExternalEventDescriptor> _externalEvents = new();
    private ScheduleDescriptor? _schedule;
    private string? _screen;
    private string? _description;
    private readonly List<ElementDescription> _elementDescriptions = new();

    /// <summary>
    /// The tag types this slice referred to by type, so the model builder can put them in the catalogue
    /// with their type (jasperfx#1297). Keyed by tag name.
    /// </summary>
    internal IReadOnlyDictionary<string, TypeDescriptor?> UsedTags => _usedTags;

    private readonly Dictionary<string, TypeDescriptor?> _usedTags = new(StringComparer.Ordinal);

    /// <summary>
    /// Create a slice builder. <see cref="EventModelBuilder.Slice"/> wires this up — author
    /// code never instantiates it directly.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    /// <param name="domain">Domain inherited from <see cref="EventModelBuilder.InDomain"/>, if any.</param>
    public EventModelSliceBuilder(string sliceName, string? domain = null) : this(sliceName, domain, null)
    {
    }

    /// <summary>
    /// Create a slice builder with an inherited domain and chapter.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    /// <param name="domain">Domain inherited from <see cref="EventModelBuilder.InDomain"/>, if any.</param>
    /// <param name="chapter">Chapter inherited from <see cref="EventModelBuilder.InChapter"/>, if any.</param>
    public EventModelSliceBuilder(string sliceName, string? domain, string? chapter)
    {
        _sliceName = sliceName;
        _domain = domain;
        _chapter = chapter;
    }

    /// <summary>
    /// The aggregate from <see cref="EventModelBuilder.ForAggregate{TAggregate}"/> in effect when this
    /// slice was opened. Applied at build time, and only to a <see cref="SlicePattern.Command"/> slice
    /// that declared no aggregate of its own (jasperfx#994).
    /// </summary>
    internal TypeDescriptor? DefaultAggregate { get; init; }

    /// <summary>
    /// Group this slice under a domain / bounded context (overrides the builder-level default). In a
    /// modular monolith, a module is a domain.
    /// </summary>
    /// <param name="domain">Domain / bounded-context name.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder InDomain(string domain)
    {
        _domain = domain;
        return this;
    }

    /// <summary>
    /// Place this slice in a chapter — a span of the timeline (overrides the builder-level default).
    /// </summary>
    /// <param name="chapter">Chapter name.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder InChapter(string chapter)
    {
        _chapter = chapter;
        return this;
    }

    /// <summary>
    /// Annotate the trigger with a human-readable label (e.g. "User clicks Place Order") —
    /// the one thing about a trigger code cannot express.
    /// </summary>
    /// <param name="label">Display label for the trigger.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy(string label)
    {
        _triggerLabel = label;
        return this;
    }

    /// <summary>Label the trigger and declare what kind of trigger it is.</summary>
    /// <param name="label">Display label for the trigger.</param>
    /// <param name="kind">What starts the slice.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy(string label, TriggerKind kind)
    {
        _triggerLabel = label;
        _triggerKind = kind;
        return this;
    }

    /// <summary>Declare what kind of trigger starts the slice.</summary>
    /// <param name="kind">What starts the slice.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy(TriggerKind kind)
    {
        _triggerKind = kind;
        return this;
    }

    /// <summary>Declare a CLR-typed trigger, such as an inbound request DTO.</summary>
    /// <param name="kind">What starts the slice.</param>
    /// <typeparam name="T">The trigger type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy<T>(TriggerKind kind)
    {
        _triggerKind = kind;
        _triggerType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>Which pattern the slice is: one of the four canonical patterns, or a Query.</summary>
    /// <param name="pattern">The slice pattern.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder Pattern(SlicePattern pattern)
    {
        _pattern = pattern;
        return this;
    }

    /// <summary>The command / inbound message type.</summary>
    public EventModelSliceBuilder Command<T>() => setCommand(TypeDescriptor.For(typeof(T)));

    /// <summary>The command / inbound message, by name, before its type exists.</summary>
    public EventModelSliceBuilder Command(string name) => setCommand(Declared(name));

    /// <summary>The handler or endpoint type that processes the command — distinct from the aggregate.</summary>
    public EventModelSliceBuilder HandledBy<T>() => setHandler(TypeDescriptor.For(typeof(T)));

    /// <summary>The handler or endpoint, by name, before its type exists.</summary>
    public EventModelSliceBuilder HandledBy(string name) => setHandler(Declared(name));

    /// <summary>An aggregate the slice decides against. Call once per aggregate.</summary>
    public EventModelSliceBuilder Against<T>() => addAggregate(TypeDescriptor.For(typeof(T)));

    /// <summary>An aggregate the slice decides against, by name, before its type exists.</summary>
    public EventModelSliceBuilder Against(string name) => addAggregate(Declared(name));

    /// <summary>Same as <see cref="Against{T}"/>.</summary>
    public EventModelSliceBuilder UsesAggregate<T>() => Against<T>();

    /// <summary>Same as <see cref="Against(string)"/>.</summary>
    public EventModelSliceBuilder UsesAggregate(string name) => Against(name);

    /// <summary>
    /// The aggregate whose stream this slice <em>starts</em> (as against appends to). Also records it as
    /// an aggregate of the slice.
    /// </summary>
    public EventModelSliceBuilder StartsStream<T>() => startStream(TypeDescriptor.For(typeof(T)));

    /// <summary>The aggregate whose stream this slice starts, by name, before its type exists.</summary>
    public EventModelSliceBuilder StartsStream(string name) => startStream(Declared(name));

    /// <summary>
    /// This slice deliberately decides against no aggregate — legitimate only for a slice that purely
    /// starts a stream or decides against nothing at all (jasperfx#994). Overrides the
    /// <see cref="EventModelBuilder.ForAggregate{TAggregate}"/> default, and tells downstream tools
    /// (Wolverine's scaffold) not to warn about a missing aggregate.
    /// </summary>
    /// <exception cref="InvalidOperationException">The slice already declares an aggregate.</exception>
    public EventModelSliceBuilder NoAggregate()
    {
        if (_aggregateTypes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' already declares an aggregate ({string.Join(", ", _aggregateTypes.Select(x => x.Name))}), so it cannot also declare NoAggregate()");
        }

        _noAggregate = true;
        return this;
    }

    /// <summary>
    /// This slice decides through the Dynamic Consistency Boundary decider model <typeparamref name="T"/>
    /// rather than single-stream aggregates (jasperfx#994). Overrides the
    /// <see cref="EventModelBuilder.ForAggregate{TAggregate}"/> default.
    /// </summary>
    /// <remarks>
    /// It records the decider type on <see cref="EventModelSliceDescriptor.DeciderModel"/>. A decider model
    /// is a self-aggregate by default (JasperFx/wolverine#4865): Wolverine's scaffold folds what the slice
    /// emits into <typeparamref name="T"/>, and fetches it by the tags the command's strong-typed ids name.
    /// </remarks>
    public EventModelSliceBuilder DeciderModel<T>() => deciderModel(TypeDescriptor.For(typeof(T)));

    /// <summary>The DCB decider model, by name, before its type exists.</summary>
    public EventModelSliceBuilder DeciderModel(string name) => deciderModel(Declared(name));

    /// <summary>
    /// A synonym for <see cref="DeciderModel{T}"/>, in the vocabulary of Wolverine's <c>[DcbModel]</c>: this
    /// slice decides through the Dynamic Consistency Boundary model <typeparamref name="T"/>.
    /// </summary>
    public EventModelSliceBuilder DcbModel<T>() => DeciderModel<T>();

    /// <summary>A synonym for <see cref="DeciderModel(string)"/>: the DCB model, by name, before its type exists.</summary>
    public EventModelSliceBuilder DcbModel(string name) => DeciderModel(name);

    /// <summary>
    /// How this view's read model is projected (JasperFx/wolverine#4865) — what code generation needs to
    /// know, not Event Modeling proper. A view that says nothing is single-stream.
    /// </summary>
    public EventModelSliceBuilder ProjectedAs(ViewProjection projection)
    {
        _viewProjection = projection;
        return this;
    }

    /// <summary>The view is one read model per stream, folded from that stream's events: the default.</summary>
    public EventModelSliceBuilder AsSingleStream() => ProjectedAs(ViewProjection.SingleStream);

    /// <summary>The view is one read model per identity that events from many streams are grouped into.</summary>
    public EventModelSliceBuilder AsMultiStream() => ProjectedAs(ViewProjection.MultiStream);

    /// <summary>The view is folded over the events a Dynamic Consistency Boundary tag query selects.</summary>
    public EventModelSliceBuilder AsDcbModel() => ProjectedAs(ViewProjection.DcbModel);

    /// <summary>An event the slice emits. Call once per event.</summary>
    public EventModelSliceBuilder Emits<T>() => emit(TypeDescriptor.For(typeof(T)));

    /// <summary>An event the slice emits, by name, before its type exists.</summary>
    public EventModelSliceBuilder Emits(string name) => emit(Declared(name));

    /// <summary>
    /// The stream lane of the event declared by the <see cref="Emits{T}"/> just before this call: which
    /// aggregate's stream it goes to (jasperfx#1203). Optional; without it tooling infers the stream from
    /// the aggregates' <c>Apply</c> and <c>Create</c> methods.
    /// </summary>
    /// <remarks>
    /// The aggregate must be one the slice decides against (<see cref="Against{T}"/>) or starts
    /// (<see cref="StartsStream{T}"/>), declared anywhere on the slice. Not to be confused with
    /// <see cref="On{T}"/>, which declares an event the slice reacts to.
    /// </remarks>
    /// <example>
    /// <code>
    /// model.Command&lt;ConvertLeadToAccount&gt;()
    ///     .Against&lt;Lead&gt;()
    ///     .StartsStream&lt;Account&gt;()
    ///     .Emits&lt;LeadConvertedToAccount&gt;().OnStream&lt;Lead&gt;()
    ///     .Emits&lt;AccountCreated&gt;().OnStream&lt;Account&gt;();
    /// </code>
    /// </example>
    /// <typeparam name="TAggregate">The aggregate whose stream the event goes to.</typeparam>
    /// <exception cref="InvalidOperationException">
    /// No event has been declared with <c>Emits</c> yet, or the event is already in a different stream lane.
    /// </exception>
    public EventModelSliceBuilder OnStream<TAggregate>() => onStream(TypeDescriptor.For(typeof(TAggregate)));

    /// <summary>The stream lane of the last emitted event, by aggregate name, before its type exists.</summary>
    public EventModelSliceBuilder OnStream(string aggregateName) => onStream(Declared(aggregateName));

    /// <summary>
    /// The event declared by the <see cref="Emits{T}"/> just before this call carries the consistency tag
    /// <typeparamref name="TTag"/> (jasperfx#1297). Call once per tag; an event may carry several. The tag joins
    /// the model's catalogue (<see cref="EventModelDescriptor.Tags"/>) under
    /// <see cref="EventTagDescriptor.NameFor(Type)"/> if it is not there yet.
    /// </summary>
    /// <example>
    /// <code>
    /// model.Command&lt;EnrolStudent&gt;()
    ///     .Emits&lt;StudentEnrolled&gt;().TaggedBy&lt;CourseId&gt;().TaggedBy&lt;StudentId&gt;();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">No event has been declared with <c>Emits</c> yet.</exception>
    public EventModelSliceBuilder TaggedBy<TTag>() => taggedBy(useTag(typeof(TTag)));

    /// <summary>The last emitted event carries the tag named <paramref name="tag"/>, before the tag type exists.</summary>
    public EventModelSliceBuilder TaggedBy(string tag) => taggedBy(useTag(tag));

    /// <summary>A non-event message the slice publishes. Call once per message.</summary>
    public EventModelSliceBuilder Publishes<T>() => add(_publishedMessages, TypeDescriptor.For(typeof(T)));

    /// <summary>A non-event message the slice publishes, by name, before its type exists.</summary>
    public EventModelSliceBuilder Publishes(string name) => add(_publishedMessages, Declared(name));

    /// <summary>
    /// An event the slice reacts to or reads — an automation's trigger event, or an event a view folds.
    /// Call once per event.
    /// </summary>
    public EventModelSliceBuilder On<T>() => consume(TypeDescriptor.For(typeof(T)));

    /// <summary>An event the slice reacts to or reads, by name, before its type exists.</summary>
    public EventModelSliceBuilder On(string name) => consume(Declared(name));

    /// <summary>
    /// The event declared by the <see cref="On{T}"/> / <see cref="From{T}"/> just before this call comes from
    /// outside the model: an external system's event, translated in (jasperfx#1298). Ties the consumed event to
    /// the external system named <paramref name="externalSystem"/>, which joins the slice's
    /// <see cref="ExternalSystem"/>s as an inbound system if it is not there yet.
    /// </summary>
    /// <example>
    /// <code>
    /// model.Translation("RecordPayment")
    ///     .From&lt;PaymentCaptured&gt;().External("Payments")
    ///     .Emits&lt;PaymentRecorded&gt;();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">No event has been declared with <c>On</c> / <c>From</c> yet.</exception>
    public EventModelSliceBuilder External(string externalSystem)
    {
        if (string.IsNullOrWhiteSpace(externalSystem))
        {
            throw new ArgumentException("An external event needs the name of the system it comes from", nameof(externalSystem));
        }

        if (_lastConsumed is null)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' marks an event as external from '{externalSystem}' before any consumed event. Call External() right after the On() or From() it applies to");
        }

        var existing = _externalEvents.FirstOrDefault(x => EventModelSliceDescriptor.SameType(x.Event, _lastConsumed));
        if (existing is not null)
        {
            if (string.Equals(existing.ExternalSystem, externalSystem, StringComparison.Ordinal)) return this;

            throw new InvalidOperationException(
                $"Slice '{_sliceName}' already says {_lastConsumed.Name} comes from {existing.ExternalSystem}, so it cannot also come from {externalSystem}");
        }

        _externalEvents.Add(new ExternalEventDescriptor(_lastConsumed, externalSystem));

        if (!_externalSystems.Any(x => x.Direction == ExternalSystemDirection.Inbound
                                       && string.Equals(x.Name, externalSystem, StringComparison.Ordinal)))
        {
            _externalSystems.Add(new ExternalSystemDescriptor(externalSystem, ExternalSystemDirection.Inbound));
        }

        return this;
    }

    /// <summary>Same as <see cref="On{T}"/>, reading better on a view: <c>View&lt;X&gt;().From&lt;E&gt;()</c>.</summary>
    public EventModelSliceBuilder From<T>() => On<T>();

    /// <summary>Same as <see cref="On(string)"/>.</summary>
    public EventModelSliceBuilder From(string name) => On(name);

    /// <summary>
    /// A read model the slice reads from — the "given the screen shows this" of a decision, or what a
    /// <see cref="SlicePattern.Query"/> slice reads and returns (jasperfx#1003). Follow it with
    /// <see cref="By{TTag}()"/> to read it <em>by tag</em>, inside a consistency boundary (jasperfx#1297).
    /// </summary>
    public EventModelSliceBuilder Reads<T>() => read(BoundaryReadKind.Projection, TypeDescriptor.For(typeof(T)), _readsFrom);

    /// <summary>A read model the slice reads from, by name, before its type exists.</summary>
    public EventModelSliceBuilder Reads(string name) => read(BoundaryReadKind.Projection, Declared(name), _readsFrom);

    /// <summary>
    /// This slice decides through the Dynamic Consistency Boundary decider model <typeparamref name="TModel"/>,
    /// read by tag (jasperfx#1297). The same as <see cref="DeciderModel{T}"/>, plus a read of the model that
    /// <see cref="ByTag{TTag}()"/> binds to a tag — the dcb.events rule that a command declares what it reads and
    /// the boundary is derived.
    /// </summary>
    /// <example>
    /// <code>
    /// model.Command&lt;EnrolStudent&gt;()
    ///     .Decides&lt;CourseEnrolment&gt;().ByTag&lt;CourseId&gt;(x => x.CourseId)
    ///     .Emits&lt;StudentEnrolled&gt;().TaggedBy&lt;CourseId&gt;().TaggedBy&lt;StudentId&gt;();
    /// </code>
    /// </example>
    public EventModelSliceBuilder Decides<TModel>() => decides(TypeDescriptor.For(typeof(TModel)));

    /// <summary>The DCB decider model the slice decides through, read by tag, by name, before its type exists.</summary>
    public EventModelSliceBuilder Decides(string modelName) => decides(Declared(modelName));

    /// <summary>
    /// The read declared by the <see cref="Reads{T}()"/> or <see cref="Decides{TModel}()"/> just before this call
    /// is <em>by</em> the tag <typeparamref name="TTag"/>, whose value the command member named like the tag
    /// supplies (jasperfx#1297). Call it once per tag the read selects by.
    /// </summary>
    /// <typeparam name="TTag">The strong-typed tag, named in the catalogue by <see cref="EventTagDescriptor.NameFor(Type)"/>.</typeparam>
    public EventModelSliceBuilder By<TTag>() => by(useTag(typeof(TTag)), null);

    /// <summary>
    /// The read just before this call is by the tag <typeparamref name="TTag"/>, whose value comes from the
    /// command member <paramref name="commandMember"/> (jasperfx#1297).
    /// </summary>
    public EventModelSliceBuilder By<TTag>(string commandMember) => by(useTag(typeof(TTag)), commandMember);

    /// <summary>
    /// The read just before this call is by the tag <typeparamref name="TTag"/>, whose value comes from the
    /// command member <paramref name="member"/> names — <c>x =&gt; x.CourseId</c> (jasperfx#1297). The typed form
    /// of <see cref="By{TTag}(string)"/>, so a rename keeps the model in step.
    /// </summary>
    /// <typeparam name="TCommand">The slice's command type.</typeparam>
    /// <typeparam name="TTag">The strong-typed tag.</typeparam>
    public EventModelSliceBuilder By<TCommand, TTag>(System.Linq.Expressions.Expression<Func<TCommand, object?>> member)
        => by(useTag(typeof(TTag)), MemberNameOf(member));

    /// <summary>The read just before this call is by the tag named <paramref name="tag"/>, bound by convention.</summary>
    public EventModelSliceBuilder By(string tag) => by(useTag(tag), null);

    /// <summary>The read just before this call is by the tag named <paramref name="tag"/>, from <paramref name="commandMember"/>.</summary>
    public EventModelSliceBuilder By(string tag, string commandMember) => by(useTag(tag), commandMember);

    /// <summary>Same as <see cref="By{TTag}()"/>, reading better after <see cref="Decides{TModel}()"/>.</summary>
    public EventModelSliceBuilder ByTag<TTag>() => By<TTag>();

    /// <summary>Same as <see cref="By{TTag}(string)"/>, reading better after <see cref="Decides{TModel}()"/>.</summary>
    public EventModelSliceBuilder ByTag<TTag>(string commandMember) => By<TTag>(commandMember);

    /// <summary>Same as <see cref="By{TCommand,TTag}"/>, reading better after <see cref="Decides{TModel}()"/>.</summary>
    public EventModelSliceBuilder ByTag<TCommand, TTag>(System.Linq.Expressions.Expression<Func<TCommand, object?>> member)
        => By<TCommand, TTag>(member);

    /// <summary>Same as <see cref="By(string)"/>.</summary>
    public EventModelSliceBuilder ByTag(string tag) => By(tag);

    /// <summary>Same as <see cref="By(string, string)"/>.</summary>
    public EventModelSliceBuilder ByTag(string tag, string commandMember) => By(tag, commandMember);

    /// <summary>
    /// A projection fed by this slice's events that sits <em>outside</em> its consistency boundary — informational,
    /// consulted by no decision (jasperfx#1297). Also records it as a projection of the slice.
    /// </summary>
    public EventModelSliceBuilder UntaggedProjection<T>() => untagged(TypeDescriptor.For(typeof(T)));

    /// <summary>An untagged projection, by name, before its type exists.</summary>
    public EventModelSliceBuilder UntaggedProjection(string name) => untagged(Declared(name));

    /// <summary>
    /// A reason this command slice can refuse — "Course is full" (jasperfx#1297). Call once per reason. A
    /// specification that refuses for a reason the slice does not declare becomes a
    /// <see cref="HotspotOrigin.UndeclaredRejection"/> hotspot; Wolverine's scaffold writes a guard per reason.
    /// </summary>
    public EventModelSliceBuilder Refuses(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason needs text", nameof(reason));
        }

        reason = reason.Trim();
        if (!_rejectionReasons.Contains(reason, StringComparer.Ordinal)) _rejectionReasons.Add(reason);
        return this;
    }

    /// <summary>
    /// Who triggers the slice, by name — the actor lane of the board (jasperfx#1298). Required roles may
    /// follow with <see cref="RequiringRole"/>.
    /// </summary>
    public EventModelSliceBuilder Actor(string name, params string[] requiredRoles)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An actor needs a name", nameof(name));
        }

        _actorName = name.Trim();
        foreach (var role in requiredRoles) RequiringRole(role);
        return this;
    }

    /// <summary>
    /// An authorization role the actor needs for this slice (jasperfx#1298). With no <see cref="Actor"/> named,
    /// the actor is the trigger's label — <c>TriggeredBy("Volunteer", TriggerKind.Human).RequiringRole("volunteer")</c>.
    /// </summary>
    public EventModelSliceBuilder RequiringRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("A required role needs a name", nameof(role));
        }

        role = role.Trim();
        if (!_requiredRoles.Contains(role, StringComparer.Ordinal)) _requiredRoles.Add(role);
        return this;
    }

    /// <summary>Several roles at once; see <see cref="RequiringRole"/>.</summary>
    public EventModelSliceBuilder RequiringRoles(params string[] roles)
    {
        foreach (var role in roles) RequiringRole(role);
        return this;
    }

    /// <summary>
    /// This automation fires every <paramref name="interval"/> (jasperfx#1298). Stamps
    /// <see cref="EventModeling.TriggerKind.JobScheduler"/> as the trigger kind when nothing else has claimed one.
    /// </summary>
    public EventModelSliceBuilder Every(TimeSpan interval) => schedule(ScheduleDescriptor.EveryInterval(interval));

    /// <summary>This automation fires on the cron <paramref name="expression"/> (jasperfx#1298).</summary>
    public EventModelSliceBuilder OnCron(string expression) => schedule(ScheduleDescriptor.OnCron(expression));

    /// <summary>This automation fires on the host's schedule named <paramref name="name"/> (jasperfx#1298).</summary>
    public EventModelSliceBuilder Scheduled(string name) => schedule(ScheduleDescriptor.Named(name));

    /// <summary>
    /// The screen a human trigger acts through, by name only — "Checkout page" (jasperfx#1298). Renders as a
    /// screen card in the wireframe lane beside the trigger. No content, images or HTML.
    /// </summary>
    public EventModelSliceBuilder FromScreen(string screen)
    {
        if (string.IsNullOrWhiteSpace(screen))
        {
            throw new ArgumentException("A screen needs a name", nameof(screen));
        }

        _screen = screen.Trim();
        return this;
    }

    /// <summary>
    /// Describe the slice in plain text or markdown (jasperfx#1298). Shown on hover; carried into stubs and
    /// specs as comments. On a merge the richer text wins and nothing is ever a disagreement.
    /// </summary>
    public EventModelSliceBuilder Describe(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A description needs text", nameof(text));
        }

        _description = text.Trim();
        return this;
    }

    /// <summary>
    /// Describe one element of the slice — its command, an event, a read model — by type (jasperfx#1298).
    /// By type rather than "the last one declared", so the chain's order never changes what a description is about.
    /// </summary>
    public EventModelSliceBuilder Describe<TElement>(string text) => describe(TypeDescriptor.For(typeof(TElement)), text);

    /// <summary>Describe one element of the slice by name, before its type exists.</summary>
    public EventModelSliceBuilder Describe(string elementName, string text) => describe(Declared(elementName), text);

    /// <summary>A read model the slice produces. Call once per read model.</summary>
    public EventModelSliceBuilder Produces<T>() => add(_readModelTypes, TypeDescriptor.For(typeof(T)));

    /// <summary>A read model the slice produces, by name, before its type exists.</summary>
    public EventModelSliceBuilder Produces(string name) => add(_readModelTypes, Declared(name));

    /// <summary>A projection that consumes the slice's events. Call once per projection.</summary>
    public EventModelSliceBuilder Projects<T>() => add(_projectionTypes, TypeDescriptor.For(typeof(T)));

    /// <summary>A projection, by name, before its type exists.</summary>
    public EventModelSliceBuilder Projects(string name) => add(_projectionTypes, Declared(name));

    /// <summary>An external system on one end of the slice.</summary>
    public EventModelSliceBuilder ExternalSystem(string name, ExternalSystemDirection direction, string? endpointUri = null)
    {
        _externalSystems.Add(new ExternalSystemDescriptor(name, direction, endpointUri));
        return this;
    }

    /// <summary>
    /// Link a specification to this slice by identity (<c>{Feature}/{Scenario}</c>). Use this
    /// only for a spec the binding source cannot stamp itself — a spec that lives outside the
    /// compilation (a manual test plan, a partner's acceptance suite). Specs the Bobcat generator
    /// or a code-first runner can see are bound by them, with resolved types, and never re-typed here.
    /// </summary>
    /// <param name="specificationIdentity">The <c>{Feature}/{Scenario}</c> identity.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder LinksToSpecification(string specificationIdentity)
    {
        _specifications.Add(new SpecificationDescriptor(specificationIdentity));
        return this;
    }

    /// <summary>
    /// Link a specification by its class and scenario method — a link the IDE navigates and a rename keeps
    /// in sync (jasperfx#995). The identity is derived as Bobcat derives it
    /// (<see cref="SpecificationIdentity.For"/>), so it equals the string the
    /// <see cref="LinksToSpecification(string)"/> overload would take.
    /// </summary>
    /// <remarks>
    /// Only for a definition that can reference its specs, which the usual layout — definitions in the app,
    /// specs in a test project — rules out. There, let the specifications supply the links instead: a spec
    /// manifest joined by command type (<see cref="EventModelSpecifications.Link"/>) needs no link here at all.
    /// </remarks>
    /// <example>
    /// <code>
    /// model.Command&lt;ApplyToVolunteer&gt;()
    ///     .LinksToSpecification&lt;apply_to_volunteer&gt;(nameof(apply_to_volunteer.volunteer_application_submitted));
    /// </code>
    /// </example>
    /// <param name="scenarioMethod">The scenario method's name — write it with <c>nameof</c>.</param>
    /// <typeparam name="TSpecification">The specification class.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder LinksToSpecification<TSpecification>(string scenarioMethod)
        => LinksToSpecification(SpecificationIdentity.For(typeof(TSpecification), scenarioMethod));

    /// <summary>
    /// Mark an open question on this slice in plain prose — the modelling conversation's
    /// "we haven't decided this yet", carried into the model so it renders as a hotspot in the
    /// slice's wireframe lane instead of dying in a whiteboard photo.
    /// </summary>
    /// <remarks>
    /// Reach for this only when there is nothing to write a specification against yet. The
    /// primary hotspot mechanism is <em>a pending specification is a hotspot</em> (jasperfx#689):
    /// once the question is sharp enough to name a scenario, write the pending spec and let the
    /// binding source stamp the hotspot, so the hotspot disappears on its own the day the spec
    /// passes. Prose has no such lifecycle — nothing retires it but you.
    /// </remarks>
    /// <param name="text">The open question, in the words you would say out loud.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder Hotspot(string text)
    {
        _hotspots.Add(HotspotDescriptor.Prose(text));
        return this;
    }

    /// <summary>
    /// Declare roles for a flow whose code this host does <em>not</em> own — a partner system's command
    /// and the events it emits, a legacy service with no Wolverine chain to derive from. Since
    /// jasperfx#957 the role methods on this builder do the same job for flows you <em>do</em> own;
    /// this remains for the case where no source will ever derive the roles.
    /// </summary>
    /// <param name="configure">Declares the externally-owned roles.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder ForFlowNotOwnedHere(Action<ExternallyOwnedRoles> configure)
    {
        _externallyOwned ??= new ExternallyOwnedRoles();
        configure(_externallyOwned);
        return this;
    }

    /// <summary>
    /// A type that does not exist yet, declared by name: no namespace, no assembly. The empty assembly
    /// is what tells the merge to match it to the real type by name (jasperfx#798).
    /// </summary>
    internal static TypeDescriptor Declared(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A declared type needs a name", nameof(name));
        }

        return new TypeDescriptor(name, name, string.Empty);
    }

    /// <summary>
    /// The member a <c>x =&gt; x.Member</c> expression names, through any boxing conversion. Anything else — a
    /// method call, an arithmetic expression — is refused: a tag value is a member of the command, not a computation.
    /// </summary>
    internal static string MemberNameOf(System.Linq.Expressions.LambdaExpression member)
    {
        var body = member.Body;
        while (body is System.Linq.Expressions.UnaryExpression { NodeType: System.Linq.Expressions.ExpressionType.Convert or System.Linq.Expressions.ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is System.Linq.Expressions.MemberExpression { Expression: System.Linq.Expressions.ParameterExpression } access)
        {
            return access.Member.Name;
        }

        throw new ArgumentException(
            $"'{member}' does not name a member of the command. Write x => x.CourseId, or pass the member's name as a string",
            nameof(member));
    }

    private string useTag(Type tagType)
    {
        var name = EventTagDescriptor.NameFor(tagType);
        _usedTags[name] = TypeDescriptor.For(tagType);
        return name;
    }

    private string useTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("A tag needs a name", nameof(tag));
        }

        tag = tag.Trim();
        _usedTags.TryAdd(tag, null);
        return tag;
    }

    private EventModelSliceBuilder taggedBy(string tag)
    {
        if (_lastEmitted is null)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' tags an event with {tag} before any emitted event. Call TaggedBy() right after the Emits() it applies to");
        }

        var index = _eventTags.FindIndex(x => EventModelSliceDescriptor.SameType(x.Event, _lastEmitted));
        if (index < 0)
        {
            _eventTags.Add(new EmittedEventTags(_lastEmitted, [tag]));
        }
        else if (!_eventTags[index].Tags.Contains(tag, StringComparer.Ordinal))
        {
            _eventTags[index] = _eventTags[index] with { Tags = [.._eventTags[index].Tags, tag] };
        }

        return this;
    }

    private EventModelSliceBuilder read(BoundaryReadKind kind, TypeDescriptor type, List<TypeDescriptor> list)
    {
        _lastBoundaryRead = (kind, type);
        return add(list, type);
    }

    private EventModelSliceBuilder decides(TypeDescriptor model)
    {
        deciderModel(model);
        _lastBoundaryRead = (BoundaryReadKind.Model, model);
        return this;
    }

    private EventModelSliceBuilder by(string tag, string? member)
    {
        if (_lastBoundaryRead is not { } last)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' reads by the {tag} tag before anything to read. Call By() right after the Reads<T>() or Decides<T>() it applies to");
        }

        var read = new BoundaryReadDescriptor(last.kind, last.type, tag, member);
        if (!_reads.Any(x => x.Kind == read.Kind && EventModelSliceDescriptor.SameType(x.Type, read.Type)
                             && string.Equals(x.Tag, read.Tag, StringComparison.Ordinal)
                             && string.Equals(x.Member, read.Member, StringComparison.Ordinal)))
        {
            _reads.Add(read);
        }

        return this;
    }

    private EventModelSliceBuilder untagged(TypeDescriptor projection)
    {
        add(_projectionTypes, projection);
        return add(_untaggedProjections, projection);
    }

    private EventModelSliceBuilder consume(TypeDescriptor type)
    {
        _lastConsumed = type;
        return add(_consumedEvents, type);
    }

    private EventModelSliceBuilder schedule(ScheduleDescriptor schedule)
    {
        _schedule = schedule;
        _triggerKind ??= EventModeling.TriggerKind.JobScheduler;
        return this;
    }

    private EventModelSliceBuilder describe(TypeDescriptor type, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A description needs text", nameof(text));
        }

        var index = _elementDescriptions.FindIndex(x => EventModelSliceDescriptor.SameType(x.Type, type));
        var description = new ElementDescription(type, text.Trim());
        if (index < 0) _elementDescriptions.Add(description);
        else _elementDescriptions[index] = description;

        return this;
    }

    /// <summary>
    /// The actor, when one was named or implied: a named <see cref="Actor"/>, else the trigger label of a
    /// <see cref="EventModeling.TriggerKind.Human"/> trigger once a role verb made it one (jasperfx#1298).
    /// </summary>
    private ActorDescriptor? resolveActor()
    {
        if (_actorName is not null) return new ActorDescriptor(_actorName, _requiredRoles.ToList());

        if (_requiredRoles.Count == 0) return null;

        if (_triggerLabel is null)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' requires the roles {string.Join(", ", _requiredRoles)} of an actor nobody named. Call Actor(\"…\") or TriggeredBy(\"…\", TriggerKind.Human) first");
        }

        return new ActorDescriptor(_triggerLabel, _requiredRoles.ToList());
    }

    private EventModelSliceBuilder emit(TypeDescriptor type)
    {
        _lastEmitted = type;
        return add(_emittedEvents, type);
    }

    private EventModelSliceBuilder onStream(TypeDescriptor aggregate)
    {
        if (_lastEmitted is null)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' declares the {aggregate.Name} stream lane before any emitted event. Call OnStream() right after the Emits() it applies to");
        }

        var existing = _emittedEventStreams.FirstOrDefault(x => EventModelSliceDescriptor.SameType(x.Event, _lastEmitted));
        if (existing is not null)
        {
            if (EventModelSliceDescriptor.SameType(existing.Aggregate, aggregate)) return this;

            throw new InvalidOperationException(
                $"Slice '{_sliceName}' already puts {_lastEmitted.Name} on the {existing.Aggregate.Name} stream, so it cannot also put it on the {aggregate.Name} stream");
        }

        _emittedEventStreams.Add(new EmittedEventStream(_lastEmitted, aggregate));
        return this;
    }

    // jasperfx#1203: a stream lane has to name an aggregate the slice decides against or starts. Checked at
    // build time because Against / StartsStream may come after the Emits in the chain.
    private void assertStreamLanes(IReadOnlyList<TypeDescriptor> aggregates)
    {
        foreach (var lane in _emittedEventStreams)
        {
            if (aggregates.Any(x => EventModelSliceDescriptor.SameType(x, lane.Aggregate))) continue;

            throw new InvalidOperationException(
                $"Slice '{_sliceName}' puts {lane.Event.Name} on the {lane.Aggregate.Name} stream, but the slice neither decides against nor starts {lane.Aggregate.Name}. Declare it with Against<{lane.Aggregate.Name}>() or StartsStream<{lane.Aggregate.Name}>()");
        }
    }

    private EventModelSliceBuilder setCommand(TypeDescriptor type)
    {
        _commandType = type;
        return this;
    }

    private EventModelSliceBuilder setHandler(TypeDescriptor type)
    {
        _handlerType = type;
        return this;
    }

    private EventModelSliceBuilder startStream(TypeDescriptor type)
    {
        if (_startsStream is not null && !_startsStream.Equals(type))
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' already starts the {_startsStream.Name} stream, so it cannot also start the {type.Name} stream. A slice starts one stream; model the other as a separate slice");
        }

        _startsStream = type;
        return addAggregate(type);
    }

    private EventModelSliceBuilder deciderModel(TypeDescriptor type)
    {
        _deciderModel = type;
        return this;
    }

    private EventModelSliceBuilder addAggregate(TypeDescriptor type)
    {
        if (_noAggregate)
        {
            throw new InvalidOperationException(
                $"Slice '{_sliceName}' declares NoAggregate(), so it cannot also decide against {type.Name}");
        }

        return add(_aggregateTypes, type);
    }

    /// <summary>
    /// The slice's aggregates and why it has them (jasperfx#994). An explicit declaration wins, then
    /// NoAggregate, then a decider model (which also wins the declaration beside explicit aggregates); only a command slice that said none of those takes the default.
    /// </summary>
    private (IReadOnlyList<TypeDescriptor> aggregates, AggregateDeclaration? declaration) resolveAggregates()
    {
        // A decider that decides which stream to start (or which aggregate to act on) is a real DCB shape
        // (jasperfx#1115): keep the explicit aggregates, but the decider role wins the declaration so the
        // descriptor, and tooling reading it, still says the slice is a DCB decision.
        if (_aggregateTypes.Count > 0)
        {
            return (_aggregateTypes.ToList(),
                _deciderModel is not null ? AggregateDeclaration.DeciderModel : AggregateDeclaration.Explicit);
        }

        if (_noAggregate) return (Array.Empty<TypeDescriptor>(), AggregateDeclaration.None);
        if (_deciderModel is not null) return (Array.Empty<TypeDescriptor>(), AggregateDeclaration.DeciderModel);

        if (_pattern == SlicePattern.Command && DefaultAggregate is not null)
        {
            return ([DefaultAggregate], AggregateDeclaration.Default);
        }

        return (Array.Empty<TypeDescriptor>(), null);
    }

    private EventModelSliceBuilder add(List<TypeDescriptor> list, TypeDescriptor type)
    {
        if (!list.Any(x => EventModelSliceDescriptor.SameType(x, type))) list.Add(type);
        return this;
    }

    internal EventModelSliceDescriptor Build()
    {
        var (aggregates, aggregateDeclaration) = resolveAggregates();
        assertStreamLanes(aggregates);

        var slice = EventModelSliceDescriptor.Named(_sliceName) with
        {
            TriggerLabel = _triggerLabel,
            TriggerType = _triggerType,
            CommandType = _commandType,
            HandlerType = _handlerType,
            Domain = _domain,
            Chapter = _chapter,
            Specifications = _specifications.ToList(),
            Hotspots = _hotspots.ToList(),
            Pattern = _pattern,
            TriggerKind = _triggerKind,
            StartsStream = _startsStream,
            AggregateTypes = aggregates,
            AggregateDeclaration = aggregateDeclaration,
            DeciderModel = _deciderModel,
            ViewProjection = _viewProjection,
            EmittedEvents = _emittedEvents.ToList(),
            EmittedEventStreams = _emittedEventStreams.ToList(),
            PublishedMessages = _publishedMessages.ToList(),
            ConsumedEvents = _consumedEvents.ToList(),
            ReadsFrom = _readsFrom.ToList(),
            ReadModelTypes = _readModelTypes.ToList(),
            ProjectionTypes = _projectionTypes.ToList(),
            ExternalSystems = _externalSystems.ToList(),
            EventTags = _eventTags.ToList(),
            Reads = _reads.ToList(),
            UntaggedProjections = _untaggedProjections.ToList(),
            RejectionReasons = _rejectionReasons.ToList(),
            Actor = resolveActor(),
            ExternalEvents = _externalEvents.ToList(),
            Schedule = _schedule,
            Screen = _screen,
            Description = _description,
            ElementDescriptions = _elementDescriptions.ToList(),
        };

        return _externallyOwned is null ? slice : slice.Merge(_externallyOwned.ToSlice(_sliceName));
    }
}

/// <summary>
/// Roles declared through <see cref="EventModelSliceBuilder.ForFlowNotOwnedHere"/> — the
/// explicitly-documented escape hatch for flows this host's code does not implement and no
/// source can therefore derive. Everything here is a role a source would otherwise stamp.
/// </summary>
public sealed class ExternallyOwnedRoles
{
    private SlicePattern? _pattern;
    private TriggerKind? _triggerKind;
    private TypeDescriptor? _triggerType;
    private TypeDescriptor? _commandType;
    private TypeDescriptor? _handlerType;
    private readonly List<TypeDescriptor> _aggregateTypes = new();
    private readonly List<TypeDescriptor> _emittedEvents = new();
    private readonly List<TypeDescriptor> _publishedMessages = new();
    private readonly List<TypeDescriptor> _projectionTypes = new();
    private readonly List<TypeDescriptor> _readModelTypes = new();
    private readonly List<ExternalSystemDescriptor> _externalSystems = new();

    /// <summary>Which of the four canonical patterns the flow is.</summary>
    public ExternallyOwnedRoles Pattern(SlicePattern pattern)
    {
        _pattern = pattern;
        return this;
    }

    /// <summary>What starts the flow.</summary>
    public ExternallyOwnedRoles TriggeredBy(TriggerKind kind)
    {
        _triggerKind = kind;
        return this;
    }

    /// <summary>A CLR-typed trigger (e.g. an inbound request DTO).</summary>
    public ExternallyOwnedRoles TriggeredBy<T>(TriggerKind kind)
    {
        _triggerKind = kind;
        _triggerType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>The command / inbound message type.</summary>
    public ExternallyOwnedRoles Command<T>()
    {
        _commandType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>The handler type, distinct from the aggregate(s).</summary>
    public ExternallyOwnedRoles HandledBy<T>()
    {
        _handlerType = TypeDescriptor.For(typeof(T));
        return this;
    }

    /// <summary>An aggregate / projected write model the handler decides against. Call once per aggregate.</summary>
    public ExternallyOwnedRoles UsesAggregate<T>()
    {
        _aggregateTypes.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>An event the flow emits. Call once per event type.</summary>
    public ExternallyOwnedRoles Emits<T>()
    {
        _emittedEvents.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>A non-event message the flow publishes. Call once per message type.</summary>
    public ExternallyOwnedRoles Publishes<T>()
    {
        _publishedMessages.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>A projection that consumes the flow's events. Call once per projection.</summary>
    public ExternallyOwnedRoles Projects<T>()
    {
        _projectionTypes.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>A read model the flow reads from or produces. Call once per read model.</summary>
    public ExternallyOwnedRoles Reads<T>()
    {
        _readModelTypes.Add(TypeDescriptor.For(typeof(T)));
        return this;
    }

    /// <summary>An external system on one end of the flow.</summary>
    public ExternallyOwnedRoles ExternalSystem(string name, ExternalSystemDirection direction, string? endpointUri = null)
    {
        _externalSystems.Add(new ExternalSystemDescriptor(name, direction, endpointUri));
        return this;
    }

    internal EventModelSliceDescriptor ToSlice(string sliceName)
        => new(
            sliceName,
            null,
            _triggerType,
            _commandType,
            _handlerType,
            _emittedEvents.ToList(),
            _projectionTypes.ToList(),
            _readModelTypes.ToList())
        {
            Pattern = _pattern,
            TriggerKind = _triggerKind,
            AggregateTypes = _aggregateTypes.ToList(),
            PublishedMessages = _publishedMessages.ToList(),
            ExternalSystems = _externalSystems.ToList(),
        };
}
