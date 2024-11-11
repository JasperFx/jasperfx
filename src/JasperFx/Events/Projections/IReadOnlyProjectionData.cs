namespace JasperFx.Events.Projections;

/// <summary>
///     Read-only diagnostic view of a registered projection
/// </summary>
public interface IReadOnlyProjectionData
{
    /// <summary>
    ///     The configured projection name used within the Async Daemon
    ///     progress tracking
    /// </summary>
    string ProjectionName { get; }

    /// <summary>
    ///     When is this projection executed?
    /// </summary>
    ProjectionLifecycle Lifecycle { get; }

    /// <summary>
    ///     The concrete .Net type implementing this projection
    /// </summary>
    Type ProjectionType { get; }
    
    /// <summary>
    /// Specify that this projection is a non 1 version of the original projection definition to opt
    /// into Marten's parallel blue/green deployment of this projection.
    /// </summary>
    public uint ProjectionVersion { get; }
}
