using System;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// A document with a <see cref="Guid" /> identity — the default identity style in every Critter
/// Stack store.
/// </summary>
/// <remarks>
/// Deliberately a mutable POCO with a plain <c>Id</c> property, which is the one document shape all
/// three stores' identity conventions agree on. <c>[Identity]</c> attributes and non-conventional
/// identity members are product-specific configuration and stay out of scope for the document
/// contract; strong-typed identifiers came into scope with jasperfx#665 and have their own document
/// in <see cref="ComplianceCoupon" />.
/// </remarks>
public class ComplianceWidget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int Weight { get; set; }
}

/// <summary>
/// A document with a <see cref="string" /> identity, so the suites can hold both
/// <c>LoadAsync</c> / <c>Delete</c> identity overloads to the same definition.
/// </summary>
public class ComplianceGadget
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Weight { get; set; }
}

/// <summary>
/// A strong-typed identifier wrapping a <see cref="Guid" /> — the canonical shape all three stores'
/// value-type support agrees on.
/// </summary>
/// <remarks>
/// A <c>record struct</c> over a single positional member, which is what every store's value-type
/// detection looks for. Which primitive it wraps is a product concern and is not held to a shared
/// definition here; what is shared is that a document keyed by a wrapper must be loadable by that
/// wrapper.
/// </remarks>
public readonly record struct CouponCode(Guid Value);

/// <summary>
/// A document keyed by a strong-typed identifier, so the suites can hold
/// <see cref="Documents.IDocumentReadOperations.LoadAsync{T}(object,System.Threading.CancellationToken)" />
/// to a definition (jasperfx#665).
/// </summary>
/// <remarks>
/// The one compliance document whose type alone is not enough to configure a store: its identity
/// type has to be registered too, which is why <see cref="DocumentComplianceConfig.ValueTypes" />
/// exists. A suite using this document registers both.
/// </remarks>
public class ComplianceCoupon
{
    public CouponCode Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int PercentOff { get; set; }
}
