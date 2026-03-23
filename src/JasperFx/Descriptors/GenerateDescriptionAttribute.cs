namespace JasperFx.Descriptors;

/// <summary>
/// Marks a class or record for compile-time OptionsDescription generation.
/// A source generator will emit a partial implementation of IDescribeMyself.ToDescription()
/// that builds the OptionsDescription without using Reflection.
/// Respects [IgnoreDescription] and [ChildDescription] on properties.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class GenerateDescriptionAttribute : Attribute
{
}
