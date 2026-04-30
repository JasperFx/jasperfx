namespace JasperFx.Events.Projections;

/// <summary>
/// Interface for projections applied "Inline" as part of saving a transaction
/// </summary>
public interface IInlineProjection<TOperations>
{
    /// <summary>
    ///     Apply inline projections during asynchronous operations.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The <paramref name="streams"/> parameter is typed as
    ///         <see cref="IEnumerable{T}"/> so that callers (notably Marten's
    ///         <c>RichEventAppender</c> and <c>QuickEventAppender</c>) can pass
    ///         the session's underlying <c>StreamAction</c> collection directly
    ///         instead of materializing a new <c>List&lt;StreamAction&gt;</c> on
    ///         every <c>SaveChangesAsync</c> call. Implementations that need
    ///         to enumerate twice or take a count should call
    ///         <c>streams.ToList()</c> once locally.
    ///     </para>
    ///     <para>
    ///         Signature widened in JasperFx.Events 2.0 — what was
    ///         <c>IReadOnlyList&lt;StreamAction&gt;</c> is now
    ///         <c>IEnumerable&lt;StreamAction&gt;</c>. Source-incompatible for
    ///         implementers; a recompile is required.
    ///     </para>
    /// </remarks>
    /// <param name="operations"></param>
    /// <param name="streams"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyAsync(TOperations operations, IEnumerable<StreamAction> streams,
        CancellationToken cancellation);
}
