namespace JasperFx.Events.Projections;

/// <summary>
/// Alternative to mark aggregation projections as being versioned to
/// opt into blue/green deployment support for projections
/// </summary>
/// <param name="version"></param>
[AttributeUsage(AttributeTargets.Class)]
public class ProjectionVersionAttribute(uint version): Attribute
{
    public uint Version { get; } = version;
}
