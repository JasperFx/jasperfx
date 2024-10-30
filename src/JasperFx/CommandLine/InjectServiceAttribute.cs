namespace JasperFx.CommandLine;

/// <summary>
/// Decorate JasperFx commands that are being called by 
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class InjectServiceAttribute : Attribute
{
    
}