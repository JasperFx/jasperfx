namespace JasperFx.Core;

/// <summary>
/// Use to direct JasperFx type scanning to ignore this type
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public class JasperFxIgnoreAttribute : Attribute
{
}