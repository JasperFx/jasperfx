namespace JasperFx;

/// <summary>
///     Marks a field, property or record parameter that holds personally identifiable information
///     (jasperfx#1299).
/// </summary>
/// <remarks>
///     Read by the Event Model's field shapes (<c>FieldDescriptor.Pii</c>), so a canvas or an export can mark
///     the field. A marker only: it changes nothing about how the value is stored or masked. Beside
///     <see cref="IdentityAttribute"/> and <see cref="GeneratedAttribute"/> because the three are read together.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public class PiiAttribute : Attribute
{
}
