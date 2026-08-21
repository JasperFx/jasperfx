using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Per-slice overlay builder. The methods here <em>name, group, annotate and link</em> —
/// they never declare a role. Roles are derived by the sources that can see them and the
/// overlay is merged onto the derived slice by name (jasperfx#687 reshape).
/// </summary>
public class EventModelSliceBuilder
{
    private readonly string _sliceName;
    private string? _domain;
    private string? _triggerLabel;
    private readonly List<SpecificationDescriptor> _specifications = new();
    private ExternallyOwnedRoles? _externallyOwned;

    /// <summary>
    /// Create a slice builder. <see cref="EventModelBuilder.Slice"/> wires this up — author
    /// code never instantiates it directly.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    /// <param name="domain">Domain inherited from <see cref="EventModelBuilder.InDomain"/>, if any.</param>
    public EventModelSliceBuilder(string sliceName, string? domain = null)
    {
        _sliceName = sliceName;
        _domain = domain;
    }

    /// <summary>
    /// Group this slice under a domain / bounded context (overrides the builder-level default).
    /// </summary>
    /// <param name="domain">Domain / bounded-context name.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder InDomain(string domain)
    {
        _domain = domain;
        return this;
    }

    /// <summary>
    /// Annotate the trigger with a human-readable label (e.g. "User clicks Place Order") —
    /// the one thing about a trigger code cannot express. The trigger's <em>kind</em> and type
    /// are derived.
    /// </summary>
    /// <param name="label">Display label for the trigger.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder TriggeredBy(string label)
    {
        _triggerLabel = label;
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
    /// <b>Escape hatch.</b> Declare roles for a flow whose code this host does <em>not</em> own —
    /// a partner system's command and the events it emits, a legacy service with no Wolverine
    /// chain to derive from. For anything this host's own code implements, do not use this:
    /// roles are derived from the code and a declaration here would only drift from it.
    /// </summary>
    /// <param name="configure">Declares the externally-owned roles.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelSliceBuilder ForFlowNotOwnedHere(Action<ExternallyOwnedRoles> configure)
    {
        _externallyOwned ??= new ExternallyOwnedRoles();
        configure(_externallyOwned);
        return this;
    }

    internal EventModelSliceDescriptor Build()
    {
        var overlay = EventModelSliceDescriptor.Named(_sliceName) with
        {
            TriggerLabel = _triggerLabel,
            Domain = _domain,
            Specifications = _specifications.ToList(),
        };

        return _externallyOwned is null ? overlay : overlay.Merge(_externallyOwned.ToSlice(_sliceName));
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
