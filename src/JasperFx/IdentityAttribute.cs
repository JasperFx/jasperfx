namespace JasperFx;

/// <summary>
///     Designates the identity property or field on a document type that does not follow the
///     <c>id</c>/<c>Id</c> naming convention. Takes priority over the conventional "Id" lookup.
/// </summary>
/// <remarks>
/// Lifted from the marker <c>IdentityAttribute</c> in Marten (<c>Marten.Schema</c>, which
/// extended <c>MartenAttribute</c> with an empty body — the <c>Modify</c> coupling is illusory)
/// and Polecat (<c>Polecat.Attributes</c>, extended <c>Attribute</c>). Lifted as a plain marker
/// so both stores type-forward their old public names. Part of the Critter Stack 2026 dedupe
/// pillar (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class IdentityAttribute : Attribute
{
}
