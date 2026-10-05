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
    private readonly List<TypeDescriptor> _aggregateTypes = new();
    private readonly List<TypeDescriptor> _emittedEvents = new();
    private readonly List<TypeDescriptor> _publishedMessages = new();
    private readonly List<TypeDescriptor> _consumedEvents = new();
    private readonly List<TypeDescriptor> _readsFrom = new();
    private readonly List<TypeDescriptor> _readModelTypes = new();
    private readonly List<TypeDescriptor> _projectionTypes = new();
    private readonly List<ExternalSystemDescriptor> _externalSystems = new();

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

    /// <summary>Which of the four canonical patterns the slice is.</summary>
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

    /// <summary>An event the slice emits. Call once per event.</summary>
    public EventModelSliceBuilder Emits<T>() => add(_emittedEvents, TypeDescriptor.For(typeof(T)));

    /// <summary>An event the slice emits, by name, before its type exists.</summary>
    public EventModelSliceBuilder Emits(string name) => add(_emittedEvents, Declared(name));

    /// <summary>A non-event message the slice publishes. Call once per message.</summary>
    public EventModelSliceBuilder Publishes<T>() => add(_publishedMessages, TypeDescriptor.For(typeof(T)));

    /// <summary>A non-event message the slice publishes, by name, before its type exists.</summary>
    public EventModelSliceBuilder Publishes(string name) => add(_publishedMessages, Declared(name));

    /// <summary>
    /// An event the slice reacts to or reads — an automation's trigger event, or an event a view folds.
    /// Call once per event.
    /// </summary>
    public EventModelSliceBuilder On<T>() => add(_consumedEvents, TypeDescriptor.For(typeof(T)));

    /// <summary>An event the slice reacts to or reads, by name, before its type exists.</summary>
    public EventModelSliceBuilder On(string name) => add(_consumedEvents, Declared(name));

    /// <summary>Same as <see cref="On{T}"/>, reading better on a view: <c>View&lt;X&gt;().From&lt;E&gt;()</c>.</summary>
    public EventModelSliceBuilder From<T>() => On<T>();

    /// <summary>Same as <see cref="On(string)"/>.</summary>
    public EventModelSliceBuilder From(string name) => On(name);

    /// <summary>A read model the slice reads from — the "given the screen shows this" of a decision.</summary>
    public EventModelSliceBuilder Reads<T>() => add(_readsFrom, TypeDescriptor.For(typeof(T)));

    /// <summary>A read model the slice reads from, by name, before its type exists.</summary>
    public EventModelSliceBuilder Reads(string name) => add(_readsFrom, Declared(name));

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
        _startsStream = type;
        return addAggregate(type);
    }

    private EventModelSliceBuilder addAggregate(TypeDescriptor type) => add(_aggregateTypes, type);

    private EventModelSliceBuilder add(List<TypeDescriptor> list, TypeDescriptor type)
    {
        if (!list.Any(x => EventModelSliceDescriptor.SameType(x, type))) list.Add(type);
        return this;
    }

    internal EventModelSliceDescriptor Build()
    {
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
            AggregateTypes = _aggregateTypes.ToList(),
            EmittedEvents = _emittedEvents.ToList(),
            PublishedMessages = _publishedMessages.ToList(),
            ConsumedEvents = _consumedEvents.ToList(),
            ReadsFrom = _readsFrom.ToList(),
            ReadModelTypes = _readModelTypes.ToList(),
            ProjectionTypes = _projectionTypes.ToList(),
            ExternalSystems = _externalSystems.ToList(),
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
