namespace JasperFx.Events.EventModeling;

/// <summary>
/// Fluent root used by an <see cref="EventModelDefinition"/> (or an inline
/// <c>services.AddEventModel(name, configure)</c> lambda) to lay the overlay on an
/// Event Model: name slices, group them by domain, label triggers, link
/// specifications and flag open questions. Each call to <see cref="Slice"/> opens one slice.
/// </summary>
public class EventModelBuilder
{
    private readonly List<EventModelSliceBuilder> _slices = new();
    private readonly List<HotspotDescriptor> _hotspots = new();
    private string? _defaultDomain;

    /// <summary>
    /// Optional friendly name of the event model. When unset, the discovery layer falls back to
    /// the defining type's name.
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
        var slice = new EventModelSliceBuilder(sliceName, _defaultDomain);
        _slices.Add(slice);
        return slice;
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
        => new(Name ?? fallbackName, BuildSlices()) { Hotspots = _hotspots.ToList() };
}
