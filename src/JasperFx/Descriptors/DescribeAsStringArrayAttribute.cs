namespace JasperFx.Descriptors;

/// <summary>
/// Applied to an enumerable property (e.g., <c>IList&lt;T&gt;</c>) to tell
/// <see cref="OptionsDescription"/> to render it as a single
/// <see cref="OptionsValue"/> with <see cref="PropertyType.StringArray"/>,
/// using each element's <c>ToString()</c> as the string representation.
/// Without this attribute, non-string enumerables are skipped from the
/// property list entirely.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DescribeAsStringArrayAttribute : Attribute
{
}
