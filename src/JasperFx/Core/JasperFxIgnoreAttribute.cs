namespace JasperFx.Core;

/// <summary>
///     Use to direct JasperFx type scanning to ignore this type
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Property)]
public class JasperFxIgnoreAttribute : Attribute
{
}