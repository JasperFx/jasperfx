namespace JasperFx.Events.EventModeling;

/// <summary>
/// Every Event Model one service hosts, as a <em>pair</em> of service and models rather than an
/// assumption that the pair is redundant (jasperfx#837).
/// </summary>
/// <remarks>
/// <para>
/// <b>The assumption this replaces.</b> Both export paths out of a Critter Stack host — Wolverine's
/// <c>WolverineEventModelExport</c> and CritterWatch's <c>ManifestAggregator</c> — carried a single
/// <see cref="EventModelDescriptor"/> on the wire, and so folded whatever discovery assembled into one
/// model named for the service. That encodes "one service is one Event Model", and the relationship is
/// not one to one in either direction: one process can host several bounded contexts, and one business
/// flow can span several services.
/// </para>
/// <para>
/// <b>Why this is not an argument.</b> CritterWatch proved the same assumption wrong one scope up and
/// fixed it there: merging across applications turned two services' identically-named commands into one
/// slice — measured at 20 <see cref="HotspotOrigin.SourceDisagreement"/> hotspots on a 28-service dev
/// fleet, 17 of them <c>HelpDesk</c> and <c>Incidents</c> both declaring a <c>CloseIncident</c> rather
/// than any real drift. A modular monolith is that situation inside one process, now that each store
/// can name its own model through <c>StoreOptions.EventModelName</c>. The conclusion it reached is the
/// one encoded here: the fix is not to change the merge, it is to stop calling it across things that
/// are not one model.
/// </para>
/// <para>
/// <b>Where a single descriptor is genuinely required</b> — a console rendering one canvas —
/// <see cref="Find"/> lets the caller choose which model, and <see cref="Collapse"/> is the explicit,
/// recorded fold for a consumer that cannot carry more than one yet. Neither picks for you, which is
/// what "first non-default name wins" was doing.
/// </para>
/// </remarks>
/// <param name="ServiceName">The service hosting these models. The dimension above the model.</param>
/// <param name="Models">The models this service hosts, one per name, in first-appearance order.</param>
public sealed record EventModelSetDescriptor(
    string ServiceName,
    IReadOnlyList<EventModelDescriptor> Models)
{
    /// <summary>The models this service hosts. Never null (jasperfx#807's treatment).</summary>
    public IReadOnlyList<EventModelDescriptor> Models { get; init; } = Models ?? Array.Empty<EventModelDescriptor>();

    /// <summary>
    /// The set for <paramref name="serviceName"/>, folding <paramref name="descriptors"/> into one
    /// model per name with <see cref="EventModelDescriptor.GroupByName"/>.
    /// </summary>
    public static EventModelSetDescriptor For(string serviceName, IEnumerable<EventModelDescriptor> descriptors)
        => new(serviceName, EventModelDescriptor.GroupByName(descriptors));

    /// <summary>The model named <paramref name="name"/>, or null when this service hosts no such model.</summary>
    public EventModelDescriptor? Find(string name)
        => Models.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The one model, when this service hosts exactly one; null when it hosts none or several.
    /// </summary>
    /// <remarks>
    /// The honest form of the old assumption. A caller that can only render one canvas asks this
    /// first, and has an answer for the ambiguous case rather than silently picking a winner.
    /// </remarks>
    public EventModelDescriptor? Sole => Models.Count == 1 ? Models[0] : null;

    /// <summary>More than one model, so no single descriptor can represent this service without loss.</summary>
    public bool IsAmbiguous => Models.Count > 1;

    /// <summary>
    /// Fold every model into one, for a consumer whose wire cannot carry more than one yet. Lossy by
    /// construction, and says so: collapsing several models appends a
    /// <see cref="HotspotOrigin.ModelCollapse"/> hotspot naming them (jasperfx#837).
    /// </summary>
    /// <remarks>
    /// A migration aid rather than the destination. The point of jasperfx#837 is that both exporters
    /// did this implicitly and reported nothing; doing it explicitly, at the caller's choice, with the
    /// loss recorded on the model is the least a consumer stuck on the old shape should do.
    /// </remarks>
    /// <param name="name">Name for the collapsed model. Defaults to <see cref="ServiceName"/>.</param>
    public EventModelDescriptor Collapse(string? name = null)
    {
        var collapsed = EventModelDescriptor.Merge(name ?? ServiceName, Models);

        if (!IsAmbiguous) return collapsed;

        var hotspot = HotspotDescriptor.ModelCollapse(ServiceName, Models.Select(x => x.Name));

        return collapsed with { Hotspots = [..collapsed.Hotspots, hotspot] };
    }
}
