using AFHSync.Api.DTOs;

namespace AFHSync.Api.Services;

/// <summary>
/// Phase 3 (§3.4): serves a (page, pageSize) window from a forward-only source such as a Graph
/// PageIterator — Graph /users has no $skip, so the window skips (page-1)*pageSize items, keeps
/// pageSize, and asks the caller to stop as soon as it has seen one more (that extra item is
/// what makes <see cref="HasMore"/> true).
/// </summary>
public sealed class PageWindow<T>
{
    private readonly List<T> _items = [];
    private int _seen;

    public PageWindow(int page, int pageSize, int maxPageSize)
    {
        (Page, PageSize) = Paging.Clamp(page, pageSize, defaultSize: 1, max: maxPageSize);
    }

    public int Page { get; }
    public int PageSize { get; }
    public IReadOnlyList<T> Items => _items;
    public bool HasMore { get; private set; }

    private int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Offers the next item. Returns true to continue iterating; false once the window is full
    /// and one extra item has been seen (the caller should stop fetching pages).
    /// </summary>
    public bool Accept(T item)
    {
        _seen++;
        if (_seen <= Skip)
            return true;
        if (_items.Count < PageSize)
        {
            _items.Add(item);
            return true;
        }
        HasMore = true;
        return false;
    }

    public PagedResult<T> ToResult() => new(_items, HasMore);
}
