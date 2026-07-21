using JasperFx.Descriptors;
using JasperFx.Events.Tags;

namespace JasperFx.Events.Descriptors;

/// <summary>
/// Diagnostic mirror of a discovered <see cref="DcbAggregateRegistration"/>, suitable for
/// the descriptor/lifecycle surface a monitoring tool (e.g. CritterWatch) reads. Carries the
/// aggregate's type identity and its tag/slice shape as wire-safe descriptors so a discovered
/// DCB aggregate — one with no registered projection name — can be surfaced as a steppable run
/// target identified by <b>type</b>. See jasperfx#546.
/// </summary>
public sealed class DcbAggregateDescriptor
{
    /// <summary>
    /// Type identity of the discovered DCB aggregate — the token a run target is identified by.
    /// </summary>
    public TypeDescriptor AggregateType { get; set; } = null!;

    /// <summary>
    /// The tag types that shape this aggregate's DCB slice, as wire-safe descriptors.
    /// </summary>
    public List<DcbTagDescriptor> Tags { get; set; } = new();

    /// <summary>
    /// Project a runtime <see cref="DcbAggregateRegistration"/> into its wire-safe descriptor.
    /// </summary>
    public static DcbAggregateDescriptor For(DcbAggregateRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return new DcbAggregateDescriptor
        {
            AggregateType = TypeDescriptor.For(registration.AggregateType),
            Tags = registration.TagTypes
                .Select(tag => new DcbTagDescriptor(
                    tag.TagType.Name,
                    tag.SimpleType.FullName ?? tag.SimpleType.Name,
                    TypeDescriptor.For(tag.TagType),
                    Description: null))
                .ToList()
        };
    }

    /// <summary>
    /// Snapshot every discovered aggregate in a registry as descriptors. Used when populating
    /// an <see cref="EventStoreUsage"/> from a store's <see cref="IDcbAggregateRegistry"/>.
    /// </summary>
    public static List<DcbAggregateDescriptor> ForRegistry(IDcbAggregateRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.Registrations.Select(For).ToList();
    }
}
