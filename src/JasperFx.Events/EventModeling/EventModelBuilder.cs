namespace JasperFx.Events.EventModeling;

/// <summary>
/// Fluent root used by an <see cref="EventModelDefinition"/> (or an inline
/// <c>services.AddEventModel(name, configure)</c> lambda) to lay the overlay on an
/// Event Model: name slices, group them by domain, label triggers and link
/// specifications. Each call to <see cref="Slice"/> opens one slice.
/// </summary>
public class EventModelBuilder
{
    private readonly List<EventModelSliceBuilder> _slices = new();
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
    /// Snapshot the whole overlay as an <see cref="EventModelDescriptor"/>.
    /// </summary>
    /// <param name="fallbackName">Name used when <see cref="Name"/> is unset.</param>
    public EventModelDescriptor Build(string fallbackName)
        => new(Name ?? fallbackName, BuildSlices());
}
