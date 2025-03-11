namespace JasperFx.Events.Projections;

/// <summary>
/// Main entry point for non-aggregation projections
/// </summary>
public interface IProjection<TOperations>
{
    /// <summary>
    /// Apply operations 
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="events">A page of new events to apply through this projection</param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation);
}