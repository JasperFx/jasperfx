using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Fluent root used by an <see cref="EventModelDefinition"/> to declare
/// the slices that make up an Event Modeling topology. Each call to
/// <see cref="Slice"/> opens a new lane the author can populate via
/// <see cref="EventModelSliceBuilder"/>.
/// </summary>
public class EventModelBuilder
{
    private readonly List<EventModelSliceBuilder> _slices = new();

    /// <summary>
    /// Optional friendly name of the event model. When unset, the
    /// discovery layer falls back to the defining type's name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Begin declaring a slice. Returns the per-slice builder so the
    /// caller can chain trigger / command / handler / events / projection
    /// / read-model entries onto it.
    /// </summary>
    /// <param name="sliceName">Display name of the slice.</param>
    public EventModelSliceBuilder Slice(string sliceName)
    {
        var slice = new EventModelSliceBuilder(sliceName);
        _slices.Add(slice);
        return slice;
    }

    /// <summary>
    /// Snapshot the configured slices as descriptor records. Called by
    /// the discovery layer once <see cref="EventModelDefinition.Configure"/>
    /// returns.
    /// </summary>
    /// <returns>A read-only list of slice descriptors in declaration order.</returns>
    public IReadOnlyList<EventModelSliceDescriptor> BuildSlices()
        => _slices.Select(x => x.Build()).ToList();
}
