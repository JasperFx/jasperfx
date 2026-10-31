using System.Collections;

namespace JasperFx.Pagination;

/// <summary>
///     The default <see cref="IPagedList{T}" />: a page of items and the metadata around it.
/// </summary>
public class PagedList<T> : IPagedList<T>
{
    private readonly IReadOnlyList<T> _items;

    public PagedList(IReadOnlyList<T> items, long totalItemCount, int pageNumber, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items;
        TotalItemCount = totalItemCount;
        PageNumber = pageNumber;
        PageSize = pageSize;

        // Fisher's guard. Polecat divided by the page size unchecked, and casting the resulting infinity
        // to an int gives an unspecified page count.
        PageCount = pageSize <= 0 ? 0 : (int)Math.Ceiling(totalItemCount / (double)pageSize);
    }

    public T this[int index] => _items[index];
    public int Count => _items.Count;
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public long TotalItemCount { get; }
    public int PageCount { get; }
    public int PageNumber { get; }
    public int PageSize { get; }

    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < PageCount;
    public bool IsFirstPage => PageNumber == 1;
    public bool IsLastPage => PageNumber >= PageCount;

    // Counted from the items, as Fisher does. Polecat counted from the total, so a page past the end
    // reported a first item that does not exist and a last item before it.
    public int FirstItemOnPage => Count == 0 ? 0 : ((PageNumber - 1) * PageSize) + 1;
    public int LastItemOnPage => Count == 0 ? 0 : FirstItemOnPage + Count - 1;
}
