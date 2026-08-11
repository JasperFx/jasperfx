using System;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// A document with a <see cref="Guid" /> identity — the default identity style in every Critter
/// Stack store.
/// </summary>
/// <remarks>
/// Deliberately a mutable POCO with a plain <c>Id</c> property, which is the one document shape all
/// three stores' identity conventions agree on. Strong-typed identifiers, <c>[Identity]</c>
/// attributes and non-conventional identity members are product-specific configuration and are out
/// of scope for the document contract.
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
