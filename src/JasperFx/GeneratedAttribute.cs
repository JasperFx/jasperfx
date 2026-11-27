namespace JasperFx;

/// <summary>
///     Marks a field, property or record parameter whose value the system generates — an id minted on the
///     way in, a timestamp, a sequence number — so no command or event has to supply it (jasperfx#1299).
/// </summary>
/// <remarks>
///     Read by the Event Model's field shapes (<c>FieldDescriptor.Generated</c>): the source generator and the
///     reflection path both set the flag from this attribute, and the information-completeness check exempts a
///     generated field from needing a source. Declared, never derived: nothing reads a method body to decide
///     that a value is generated. Beside <see cref="IdentityAttribute"/> because the two are read together.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public class GeneratedAttribute : Attribute
{
}
