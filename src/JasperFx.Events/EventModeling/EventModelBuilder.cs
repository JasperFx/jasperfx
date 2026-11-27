using JasperFx.Core.Reflection;
using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Fluent root used by an <see cref="EventModelDefinition"/> (or an inline
/// <c>services.AddEventModel(name, configure)</c> lambda) to declare an Event Model: open slices with
/// <see cref="Slice"/> or a pattern verb (<see cref="Command{TCommand}"/>, <see cref="Automation(string)"/>,
/// <see cref="View{TView}"/>, <see cref="Query{TReadModel}()"/>, <see cref="Translation"/>), declare their roles by type or by name, group
/// them by domain and chapter, declare aggregates, link specifications and flag open questions.
/// </summary>
/// <remarks>
/// Everything declared here sits on the <see cref="EventModelProvenance.Declared"/> rung, so once code
/// exists the derived model wins and any difference becomes a hotspot (jasperfx#703, jasperfx#957).
/// </remarks>
public class EventModelBuilder
{
    private readonly List<EventModelSliceBuilder> _slices = new();
    private readonly List<HotspotDescriptor> _hotspots = new();
    private readonly List<AggregateDescriptor> _aggregates = new();
    private readonly List<DomainAssignmentDescriptor> _domainAssignments = new();
    private readonly List<EventTagDescriptor> _tags = new();
    private readonly List<TypeShapeBuilder> _shapes = new();
    private string? _defaultDomain;
    private string? _defaultChapter;
    private TypeDescriptor? _defaultAggregate;
    private TypeDescriptor? _lastAggregate;

    /// <summary>
    /// Optional name of the event model. When unset, the definition's own
    /// <see cref="EventModelDefinition.Name"/> applies, and when that is unset too the model is the
    /// application's (jasperfx#992).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Domain / bounded context applied to every slice opened after this call that does not set
    /// its own with <see cref="EventModelSliceBuilder.InDomain"/>.
    /// </summary>
    /// <param name="domain">Domain / bounded-context name.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder InDomain(string domain)
    {
        _defaultDomain = domain;
        return this;
    }

    /// <summary>
    /// Mark an open question about the model as a whole in plain prose — the question that is not
    /// about any one slice ("do we even own the SLA clock?"). For a question about a single slice,
    /// use <see cref="EventModelSliceBuilder.Hotspot"/> instead so it renders on that slice.
    /// </summary>
    /// <remarks>
    /// The same caveat as the per-slice form: prefer a pending specification (jasperfx#689) once
    /// the question is sharp enough to name a scenario, because that hotspot retires itself when
    /// the spec passes. Prose stays until someone deletes the line.
    /// </remarks>
    /// <param name="text">The open question, in the words you would say out loud.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder Hotspot(string text)
    {
        _hotspots.Add(HotspotDescriptor.Prose(text));
        return this;
    }

    /// <summary>
    /// Open a slice. The <paramref name="sliceName"/> is the key the overlay merges onto the
    /// derived model by, so it should match the derived slice's name — by convention the
    /// command's short name.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    public EventModelSliceBuilder Slice(string sliceName)
    {
        var slice = new EventModelSliceBuilder(sliceName, _defaultDomain, _defaultChapter)
        {
            DefaultAggregate = _defaultAggregate
        };
        _slices.Add(slice);
        return slice;
    }

    /// <summary>
    /// Declare which handlers and endpoints belong to <paramref name="domain"/> — a module, in a modular
    /// monolith (jasperfx#960). Membership is declared, never inferred: policies declared here and
    /// <see cref="DomainAttribute"/> are the only inputs.
    /// </summary>
    /// <example>
    /// <code>
    /// model.Domain("Billing").Includes(typeof(BillingModule).Assembly);
    /// model.Domain("Shipping").IncludesNamespace("MyApp.Shipping");
    /// </code>
    /// </example>
    /// <param name="domain">The domain (module) name.</param>
    public DomainPolicyBuilder Domain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("A domain needs a name", nameof(domain));
        return new DomainPolicyBuilder(domain, _domainAssignments);
    }

    /// <summary>
    /// Chapter — a span of the timeline — applied to every slice opened after this call that does not
    /// set its own with <see cref="EventModelSliceBuilder.InChapter"/> (jasperfx#957).
    /// </summary>
    /// <param name="chapter">Chapter name.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder InChapter(string chapter)
    {
        _defaultChapter = chapter;
        return this;
    }

    /// <summary>
    /// The aggregate every <see cref="SlicePattern.Command"/> slice opened after this call decides
    /// against, unless it declares its own with <see cref="EventModelSliceBuilder.Against{T}"/>,
    /// <see cref="EventModelSliceBuilder.StartsStream{T}"/>, <see cref="EventModelSliceBuilder.NoAggregate"/>
    /// or <see cref="EventModelSliceBuilder.DeciderModel{T}"/> (jasperfx#994). Also declares the aggregate
    /// on the model, as <see cref="Aggregate{TAggregate}"/> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="InChapter"/>: the last call wins, and applies to the command slices
    /// that follow it, never to earlier ones. Views, automations and translations are untouched. Like
    /// every default here it stays inside the one definition — each definition gets a fresh builder.
    /// </para>
    /// <para>
    /// The applied aggregate is a <see cref="EventModelProvenance.Declared"/> claim, marked
    /// <see cref="AggregateDeclaration.Default"/>; code that decides against something else wins, and the
    /// difference becomes a hotspot. <see cref="Aggregate{TAggregate}"/> is deliberately unchanged and sets
    /// no default — giving it this meaning would silently assign the last-declared aggregate to every
    /// existing definition's commands.
    /// </para>
    /// </remarks>
    /// <typeparam name="TAggregate">The aggregate the following commands decide against.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder ForAggregate<TAggregate>()
        => forAggregate(TypeDescriptor.For(typeof(TAggregate)));

    /// <summary>
    /// <see cref="ForAggregate{TAggregate}"/> for an aggregate that has no type yet.
    /// </summary>
    /// <param name="aggregateName">The aggregate's name.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder ForAggregate(string aggregateName)
        => forAggregate(EventModelSliceBuilder.Declared(aggregateName));

    private EventModelBuilder forAggregate(TypeDescriptor type)
    {
        _defaultAggregate = type;
        return addAggregate(type, AggregateKind.WriteAggregate);
    }

    /// <summary>
    /// Open a <see cref="SlicePattern.Command"/> slice for <typeparamref name="TCommand"/>, named for the
    /// command — the name Wolverine derives for the same slice, so the two merge (jasperfx#957).
    /// </summary>
    public EventModelSliceBuilder Command<TCommand>()
        => Slice(SliceNameFor(typeof(TCommand))).Pattern(SlicePattern.Command).Command<TCommand>();

    /// <summary>Open a <see cref="SlicePattern.Command"/> slice for a command that has no type yet.</summary>
    /// <param name="commandName">The command's name, which also names the slice.</param>
    public EventModelSliceBuilder Command(string commandName)
        => Slice(commandName).Pattern(SlicePattern.Command).Command(commandName);

    /// <summary>
    /// Open an <see cref="SlicePattern.Automation"/> slice — the system reacting to something. Say what it
    /// reacts to with <see cref="EventModelSliceBuilder.On{T}"/>.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    public EventModelSliceBuilder Automation(string sliceName)
        => Slice(sliceName).Pattern(SlicePattern.Automation);

    /// <summary>
    /// Open an <see cref="SlicePattern.Automation"/> slice for <typeparamref name="TCommand"/> fired by the
    /// job scheduler — Wolverine's cron scheduling — named for the command, as Wolverine names it.
    /// </summary>
    public EventModelSliceBuilder Automation<TCommand>()
        => Slice(SliceNameFor(typeof(TCommand)))
            .Pattern(SlicePattern.Automation)
            .TriggeredBy(TriggerKind.JobScheduler)
            .Command<TCommand>();

    /// <summary>
    /// Open a <see cref="SlicePattern.View"/> slice producing <typeparamref name="TView"/>, named for the
    /// view — the name the store-derived source gives the same slice (jasperfx#825). Say which events it
    /// folds with <see cref="EventModelSliceBuilder.From{T}"/>.
    /// </summary>
    public EventModelSliceBuilder View<TView>()
        => Slice(SliceNameFor(typeof(TView))).Pattern(SlicePattern.View).Produces<TView>();

    /// <summary>Open a <see cref="SlicePattern.View"/> slice for a view that has no type yet.</summary>
    /// <param name="viewName">The view's name, which also names the slice.</param>
    public EventModelSliceBuilder View(string viewName)
        => Slice(viewName).Pattern(SlicePattern.View).Produces(viewName);

    /// <summary>
    /// Open a <see cref="SlicePattern.Query"/> slice — a read that changes no state (jasperfx#1003). Say what
    /// it reads with <see cref="EventModelSliceBuilder.Reads{T}"/>, and what starts it with
    /// <see cref="EventModelSliceBuilder.TriggeredBy(TriggerKind)"/>.
    /// </summary>
    /// <remarks>
    /// Slices merge by name, so to meet the slice Wolverine derives from an HTTP endpoint, use the name
    /// Wolverine gives it: the request type's name, else the verb and route (<c>"GET /api/accounts/{id}"</c>).
    /// </remarks>
    /// <param name="sliceName">Display name of the slice.</param>
    public EventModelSliceBuilder Query(string sliceName)
        => Slice(sliceName).Pattern(SlicePattern.Query);

    /// <summary>
    /// Open a <see cref="SlicePattern.Query"/> slice named <paramref name="sliceName"/> that reads
    /// <typeparamref name="TReadModel"/> — a read model, aggregate or document. The read links the query to
    /// the View slice producing <typeparamref name="TReadModel"/>.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    /// <typeparam name="TReadModel">What the query reads and returns.</typeparam>
    public EventModelSliceBuilder Query<TReadModel>(string sliceName)
        => Query(sliceName).Reads<TReadModel>();

    /// <summary>
    /// Open a <see cref="SlicePattern.Query"/> slice that reads <typeparamref name="TReadModel"/>, named
    /// <c>Get{TReadModel}</c> — <c>Query&lt;Account&gt;()</c> is the slice <c>GetAccount</c>. The name
    /// differs from the <see cref="View{TView}"/> slice for the same type on purpose: a view and the query
    /// reading it are two slices. Use <see cref="Query{TReadModel}(string)"/> to choose the name.
    /// </summary>
    /// <typeparam name="TReadModel">What the query reads and returns.</typeparam>
    public EventModelSliceBuilder Query<TReadModel>()
        => Query<TReadModel>("Get" + SliceNameFor(typeof(TReadModel)));

    /// <summary>Open a <see cref="SlicePattern.Translation"/> slice — something outside, translated in.</summary>
    /// <param name="sliceName">Display name of the slice.</param>
    public EventModelSliceBuilder Translation(string sliceName)
        => Slice(sliceName).Pattern(SlicePattern.Translation);

    /// <summary>
    /// Declare an aggregate of the model (jasperfx#957). The events it applies are derived from the code
    /// once it exists; until then the aggregate renders on its own.
    /// </summary>
    /// <param name="kind">What sort of aggregate it is.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder Aggregate<TAggregate>(AggregateKind kind = AggregateKind.WriteAggregate)
        => addAggregate(TypeDescriptor.For(typeof(TAggregate)), kind);

    /// <summary>Declare an aggregate that has no type yet.</summary>
    /// <param name="aggregateName">The aggregate's name.</param>
    /// <param name="kind">What sort of aggregate it is.</param>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder Aggregate(string aggregateName, AggregateKind kind = AggregateKind.WriteAggregate)
        => addAggregate(EventModelSliceBuilder.Declared(aggregateName), kind);

    private EventModelBuilder addAggregate(TypeDescriptor type, AggregateKind kind)
    {
        if (!_aggregates.Any(x => EventModelSliceDescriptor.SameType(x.Type, type)))
        {
            _aggregates.Add(new AggregateDescriptor(type, kind, Array.Empty<TypeDescriptor>()));
        }

        _lastAggregate = type;
        return this;
    }

    /// <summary>
    /// Declare the identity type of the aggregate declared just before — by <see cref="Aggregate{TAggregate}"/>
    /// or <see cref="ForAggregate{TAggregate}"/> — such as a strong-typed <c>OrderId</c>, <see cref="Guid"/> or
    /// <see cref="string"/> (jasperfx#1004).
    /// </summary>
    /// <example>
    /// <code>
    /// model.Aggregate&lt;Order&gt;().IdentifiedBy&lt;OrderId&gt;();
    /// </code>
    /// </example>
    /// <remarks>
    /// A <see cref="EventModelProvenance.Declared"/> claim on <see cref="AggregateDescriptor.IdentityType"/>. Once
    /// the aggregate's single-stream projection is registered, the store-derived identity wins, and a different
    /// one becomes a model-level <see cref="HotspotOrigin.SourceDisagreement"/>.
    /// </remarks>
    /// <typeparam name="TIdentity">The aggregate's identity type.</typeparam>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">No aggregate has been declared yet.</exception>
    public EventModelBuilder IdentifiedBy<TIdentity>()
        => identifiedBy(TypeDescriptor.For(typeof(TIdentity)));

    /// <summary>
    /// <see cref="IdentifiedBy{TIdentity}"/> for an identity type that has no type yet.
    /// </summary>
    /// <param name="identityName">The identity type's name.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">No aggregate has been declared yet.</exception>
    public EventModelBuilder IdentifiedBy(string identityName)
        => identifiedBy(EventModelSliceBuilder.Declared(identityName));

    private EventModelBuilder identifiedBy(TypeDescriptor identity)
    {
        var index = _lastAggregate is null
            ? -1
            : _aggregates.FindIndex(x => EventModelSliceDescriptor.SameType(x.Type, _lastAggregate));

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"IdentifiedBy({identity.Name}) needs an aggregate: call Aggregate<T>() or ForAggregate<T>() first");
        }

        _aggregates[index] = _aggregates[index] with { IdentityType = identity };
        return this;
    }

    /// <summary>
    /// Declare a consistency tag of the model — a strong-typed id events carry and Dynamic Consistency
    /// Boundary queries select by (jasperfx#1297). Named by <see cref="EventTagDescriptor.NameFor(Type)"/>:
    /// <c>CourseId</c> is the tag <c>courseId</c>. A tag an event is <c>TaggedBy</c> or a read is <c>By</c>
    /// joins the catalogue on its own; declare it here to say so up front, or to name its inner primitive.
    /// </summary>
    /// <typeparam name="TTag">The strong-typed tag.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EventModelBuilder Tag<TTag>() => addTag(EventTagDescriptor.NameFor(typeof(TTag)), TypeDescriptor.For(typeof(TTag)), null);

    /// <summary>
    /// Declare a consistency tag whose strong type is <typeparamref name="TTag"/> and whose inner primitive is
    /// <typeparamref name="TInner"/> — <c>Tag&lt;CourseId, Guid&gt;()</c> (jasperfx#1297).
    /// </summary>
    public EventModelBuilder Tag<TTag, TInner>()
        => addTag(EventTagDescriptor.NameFor(typeof(TTag)), TypeDescriptor.For(typeof(TTag)), TypeDescriptor.For(typeof(TInner)));

    /// <summary>
    /// Declare a consistency tag by name, with the primitive its values are — <c>Tag("courseId", typeof(Guid))</c>
    /// — before a strong-typed id exists for it (jasperfx#1297).
    /// </summary>
    /// <param name="name">The tag's name.</param>
    /// <param name="innerType">The primitive a tag value is.</param>
    public EventModelBuilder Tag(string name, Type innerType)
    {
        ArgumentNullException.ThrowIfNull(innerType);
        return addTag(name, null, TypeDescriptor.For(innerType));
    }

    /// <summary>Declare a consistency tag by name alone (jasperfx#1297).</summary>
    public EventModelBuilder Tag(string name) => addTag(name, null, null);

    private EventModelBuilder addTag(string name, TypeDescriptor? type, TypeDescriptor? inner)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A tag needs a name", nameof(name));
        name = name.Trim();

        var index = _tags.FindIndex(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            _tags.Add(new EventTagDescriptor(name, type) { InnerType = inner });
            return this;
        }

        var existing = _tags[index];
        if (existing.Type is not null && type is not null && !EventModelSliceDescriptor.SameType(existing.Type, type))
        {
            throw new InvalidOperationException(
                $"The tag '{name}' is already declared with the type {existing.Type.Name}, so it cannot also be {type.Name}");
        }

        _tags[index] = existing with { Type = existing.Type ?? type, InnerType = existing.InnerType ?? inner };
        return this;
    }

    /// <summary>
    /// Declare the fields of a modelled type — a command, an event, a read model — for stub-first work before
    /// the type has members (jasperfx#1299). Once the type exists, the fields derived from it win; a declared
    /// field the code lacks becomes a model-level disagreement, a derived field the declaration lacks does not.
    /// </summary>
    /// <example>
    /// <code>
    /// model.Shape&lt;OrderSummary&gt;()
    ///     .Field("Id").OfType&lt;Guid&gt;().Identity()
    ///     .Field("Total").OfType&lt;decimal&gt;().From&lt;OrderPlaced&gt;(x => x.OrderTotal)
    ///     .Field("Lines").OfType&lt;OrderLine&gt;().Many();
    /// </code>
    /// </example>
    /// <typeparam name="T">The modelled type.</typeparam>
    public TypeShapeBuilder Shape<T>() => shape(TypeDescriptor.For(typeof(T)));

    /// <summary>Declare the fields of a type that has no type yet, by name.</summary>
    public TypeShapeBuilder Shape(string typeName) => shape(EventModelSliceBuilder.Declared(typeName));

    private TypeShapeBuilder shape(TypeDescriptor type)
    {
        var existing = _shapes.FirstOrDefault(x => EventModelSliceDescriptor.SameType(x.Type, type));
        if (existing is not null) return existing;

        var builder = new TypeShapeBuilder(type);
        _shapes.Add(builder);
        return builder;
    }

    /// <summary>
    /// The slice name a type-first verb gives a type: its short name, or its short name in code for a
    /// generic — the same rule Wolverine uses when it derives the slice, so the two merge by name.
    /// </summary>
    internal static string SliceNameFor(Type type) => type.IsGenericType ? type.ShortNameInCode() : type.Name;

    /// <summary>
    /// The tag catalogue as built: what was declared with <see cref="Tag{TTag}"/>, plus every tag a slice used
    /// through <c>TaggedBy</c> or <c>By</c>, carrying its type where the slice named one (jasperfx#1297).
    /// </summary>
    private IReadOnlyList<EventTagDescriptor> buildTags()
    {
        var tags = _tags.ToList();
        foreach (var slice in _slices)
        {
            foreach (var (name, type) in slice.UsedTags)
            {
                var index = tags.FindIndex(x => string.Equals(x.Name, name, StringComparison.Ordinal));
                if (index < 0)
                {
                    tags.Add(new EventTagDescriptor(name, type));
                }
                else if (tags[index].Type is null && type is not null)
                {
                    tags[index] = tags[index] with { Type = type };
                }
            }
        }

        return tags;
    }

    /// <summary>
    /// Snapshot the configured slices as descriptor records. Called by the discovery layer once
    /// <see cref="EventModelDefinition.Configure"/> returns.
    /// </summary>
    /// <returns>A read-only list of slice descriptors in declaration order.</returns>
    public IReadOnlyList<EventModelSliceDescriptor> BuildSlices()
        => _slices.Select(x => x.Build()).ToList();

    /// <summary>
    /// Snapshot the whole overlay as an <see cref="EventModelDescriptor"/>, model-level hotspots
    /// included.
    /// </summary>
    /// <param name="fallbackName">Name used when <see cref="Name"/> is unset.</param>
    public EventModelDescriptor Build(string fallbackName)
        => new(Name ?? fallbackName, BuildSlices())
        {
            Hotspots = _hotspots.ToList(),
            Aggregates = _aggregates.ToList(),
            DomainAssignments = _domainAssignments.ToList(),
            Tags = buildTags(),
            Shapes = _shapes.Select(x => x.Build()).ToList(),
        };
}
