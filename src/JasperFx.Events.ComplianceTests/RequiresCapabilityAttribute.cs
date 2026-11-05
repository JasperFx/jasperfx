using System;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Declares that an abstract compliance suite is gated, in every fact, by one capability flag on the
/// store's fixture (jasperfx#1091). The suite bodies still call <c>Assert.SkipUnless</c> themselves;
/// this attribute only makes the same gate visible to <see cref="ComplianceSuiteInventory" />, which
/// cannot see inside a method body.
/// </summary>
/// <remarks>
/// Put it only on a suite where one flag gates the whole suite. A suite with several flags, or with
/// a flag on a few facts, is not "left out" by the flag being off, so it carries no attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresCapabilityAttribute : Attribute
{
    public RequiresCapabilityAttribute(string flagName)
    {
        FlagName = flagName;
    }

    /// <summary>
    /// The name of the public boolean property on the fixture, e.g.
    /// <c>nameof(DocumentStorageComplianceFixture.SupportsNumericRevisions)</c>.
    /// </summary>
    public string FlagName { get; }
}
