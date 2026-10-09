using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Links an assembled model's slices to their specifications from a spec manifest, instead of from links
/// typed into an <c>EventModelDefinition</c> (jasperfx#995).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and run over an assembled model.</b> A spec manifest lives with the specifications, in a test
/// assembly the application host never loads, so the join cannot be one more
/// <c>IEventModelDefinitionSource</c>: it has to see every source's slices to find the one handling a
/// command. Where it runs — a monitor that receives the runner's manifest, or a model command handed the
/// spec assembly — is the caller's choice; this is only the join.
/// </para>
/// <para>
/// <b>Derived links sit on <see cref="EventModelProvenance.Specified"/>.</b> Each linked slice is merged
/// with a slice carrying only the derived links, so the ladder decides as it does for every role: the
/// derived links outrank hand-typed ones, and a declared link they do not include becomes a
/// <see cref="HotspotOrigin.SourceDisagreement"/> hotspot rather than vanishing (jasperfx#703).
/// </para>
/// <para>
/// <b>An ambiguous join is a hotspot, not a guess.</b> A command handled by several slices, with nothing in
/// the binding to choose between them, is linked to none of them and reported as a model-level
/// <see cref="HotspotOrigin.UnresolvedSpecification"/>. So is an explicit slice marker naming a slice the
/// model does not have. A spec whose command no slice handles is simply not linked: that is a spec for
/// code that does not exist yet, which the model has nothing to say about.
/// </para>
/// </remarks>
public static class EventModelSpecifications
{
    /// <summary>
    /// Link <paramref name="model"/>'s slices to <paramref name="specifications"/> by command type (and
    /// domain or namespace when that is ambiguous), or by an explicit slice name.
    /// </summary>
    public static EventModelDescriptor Link(EventModelDescriptor model,
        IEnumerable<SpecificationBindingDescriptor> specifications)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(specifications);

        var linksBySlice = new Dictionary<string, List<SpecificationDescriptor>>(StringComparer.Ordinal);
        var hotspots = new List<HotspotDescriptor>();

        foreach (var binding in specifications)
        {
            var resolution = resolve(model.Slices, binding);
            if (resolution.Slice is { } slice)
            {
                if (!linksBySlice.TryGetValue(slice.Name, out var links))
                {
                    linksBySlice[slice.Name] = links = new List<SpecificationDescriptor>();
                }

                if (links.All(x => x.Identity != binding.Identity))
                {
                    links.Add(new SpecificationDescriptor(binding.Identity, binding.ResolvedTypes));
                }
            }
            else if (resolution.Problem is { } problem)
            {
                hotspots.Add(HotspotDescriptor.UnresolvedSpecification(binding.Identity, problem));
            }
        }

        // The derived half carries no Origin. Origin merges as a scalar between non-declared rungs, so
        // stamping one would read as the specifications disagreeing with Wolverine about where the slice
        // lives; a disagreement over the links themselves still names the Specified rung.
        var slices = model.Slices
            .Select(slice => linksBySlice.TryGetValue(slice.Name, out var links)
                ? slice.Merge(EventModelSliceDescriptor.Named(slice.Name) with
                {
                    Specifications = links,
                    Provenance = EventModelProvenance.Specified,
                })
                : slice)
            .ToList();

        var modelHotspots = model.Hotspots.ToList();
        foreach (var hotspot in hotspots)
        {
            if (!modelHotspots.Any(x => x.Origin == hotspot.Origin && x.Text == hotspot.Text)) modelHotspots.Add(hotspot);
        }

        return model with { Slices = slices, Hotspots = modelHotspots };
    }

    private readonly record struct Resolution(EventModelSliceDescriptor? Slice, string? Problem);

    private static Resolution resolve(IReadOnlyList<EventModelSliceDescriptor> slices, SpecificationBindingDescriptor binding)
    {
        if (binding.SliceName is { } sliceName)
        {
            var named = slices.FirstOrDefault(x => string.Equals(x.Name, sliceName, StringComparison.Ordinal));
            return named is null
                ? new Resolution(null, $"names slice '{sliceName}', which the model does not have")
                : new Resolution(named, null);
        }

        if (binding.CommandType is not { } command) return default;

        var candidates = slices
            .Where(x => x.CommandType is not null && EventModelSliceDescriptor.SameType(x.CommandType, command))
            .ToList();

        if (candidates.Count == 0) return default;
        if (candidates.Count == 1) return new Resolution(candidates[0], null);

        // Several slices handle this command, one per module. Narrow by what the binding says about where
        // it belongs; only a single survivor is an answer.
        var narrowed = candidates;
        if (binding.Domain is { } domain)
        {
            narrowed = narrowed.Where(x => string.Equals(x.Domain, domain, StringComparison.Ordinal)).ToList();
        }

        if (narrowed.Count > 1 && binding.Namespace is { } ns)
        {
            narrowed = narrowed.Where(x => inNamespace(x.HandlerType ?? x.CommandType, ns)).ToList();
        }

        if (narrowed.Count == 1) return new Resolution(narrowed[0], null);

        var names = string.Join(", ", (narrowed.Count == 0 ? candidates : narrowed).Select(x => x.Name));
        return new Resolution(null,
            $"exercises {command.Name}, which several slices handle ({names}); declare its domain, namespace or slice to choose one");
    }

    private static bool inNamespace(TypeDescriptor? type, string ns)
    {
        // A declared type has no namespace to compare (jasperfx#798), so it cannot be told apart this way.
        if (type is null || string.IsNullOrEmpty(type.AssemblyName)) return false;

        var lastDot = type.FullName.LastIndexOf('.');
        var typeNamespace = lastDot < 0 ? string.Empty : type.FullName[..lastDot];

        return string.Equals(typeNamespace, ns, StringComparison.Ordinal)
               || typeNamespace.StartsWith(ns + ".", StringComparison.Ordinal);
    }
}
