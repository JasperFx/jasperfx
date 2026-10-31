namespace JasperFx.Pagination;

/// <summary>
///     A page of results plus everything needed to render a pager.
/// </summary>
/// <remarks>
///     <para>
///         jasperfx#1054. Polecat's and Fisher's <c>IPagedList&lt;T&gt;</c> were the same interface.
///         Marten keeps its own, which is <see cref="long" />-typed throughout and an
///         <see cref="IEnumerable{T}" /> rather than an <see cref="IReadOnlyList{T}" />.
///     </para>
///     <para>
///         In a namespace of its own because this name is taken in the stores: a store file importing
///         <c>JasperFx.Pagination</c> beside <c>Polecat.Pagination</c> is a CS0104 at every use. No store
///         file imports this one until it adopts it.
///     </para>
/// </remarks>
public interface IPagedList<out T> : IReadOnlyList<T>
{
    /// <summary>How many items match the query, ignoring paging.</summary>
    long TotalItemCount { get; }

    /// <summary>How many pages of <see cref="PageSize" /> the total makes.</summary>
    int PageCount { get; }

    /// <summary>The 1-based page this is.</summary>
    int PageNumber { get; }

    /// <summary>The maximum number of items on a page.</summary>
    int PageSize { get; }

    /// <summary>Whether there is a page before this one.</summary>
    bool HasPreviousPage { get; }

    /// <summary>Whether there is a page after this one.</summary>
    bool HasNextPage { get; }

    /// <summary>Whether this is the first page.</summary>
    bool IsFirstPage { get; }

    /// <summary>Whether this is the last page, or past it.</summary>
    bool IsLastPage { get; }

    /// <summary>The 1-based index of this page's first item in the whole result, or 0 when empty.</summary>
    int FirstItemOnPage { get; }

    /// <summary>The 1-based index of this page's last item in the whole result, or 0 when empty.</summary>
    int LastItemOnPage { get; }
}
