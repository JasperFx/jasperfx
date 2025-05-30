namespace JasperFx.Events.Projections;

/// <summary>
/// Interface for projections applied "Inline" as part of saving a transaction
/// </summary>
public interface IInlineProjection<TOperations>
{
    /// <summary>
    ///     Apply inline projections during asynchronous operations
    /// </summary>
    /// <param name="operations"></param>
    /// <param name="streams"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(TOperations operations, IReadOnlyList<StreamAction> streams,
        CancellationToken cancellation);
}
