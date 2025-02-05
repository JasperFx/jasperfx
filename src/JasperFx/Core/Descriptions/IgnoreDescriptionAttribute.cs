namespace JasperFx.Core.Descriptions;

/// <summary>
/// Just tells the Description to ignore this property when reading property values
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class IgnoreDescriptionAttribute : Attribute
{
    
}