namespace JasperFx.Descriptors;

/// <summary>
/// Marks a property whose diagnostic value is simply whether it has been configured
/// by the user rather than the raw value. <see cref="OptionsDescription"/> will
/// render the property as "Configured" when the value is non-null, and "Default"
/// when the value is null.
///
/// Useful for delegate-valued properties (e.g. credential providers, naming
/// selectors) where the delegate itself is opaque but whether it was supplied
/// at all is meaningful diagnostic information.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DescribeAsConfigurationStateAttribute : Attribute
{
}
