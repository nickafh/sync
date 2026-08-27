using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class PageWindowTests
{
    private static (PageWindow<int> window, bool stoppedEarly) Feed(int page, int pageSize, int available)
    {
        var window = new PageWindow<int>(page, pageSize, maxPageSize: 999);
        for (var i = 1; i <= available; i++)
        {
            if (!window.Accept(i))
                return (window, true);
        }
        return (window, false);
    }

    [Fact]
    public void FirstPage_TakesPageSizeItems_AndStopsAfterOneExtra()
    {
        var (w, stopped) = Feed(page: 1, pageSize: 3, available: 10);

        Assert.Equal(new[] { 1, 2, 3 }, w.Items);
        Assert.True(w.HasMore);
        Assert.True(stopped);                  // iteration stopped at the 4th item, not the 10th
    }

    [Fact]
    public void LaterPage_SkipsEarlierItems()
    {
        var (w, _) = Feed(page: 3, pageSize: 3, available: 10);

        Assert.Equal(new[] { 7, 8, 9 }, w.Items);
        Assert.True(w.HasMore);
    }

    [Fact]
    public void LastPage_HasMoreFalse_WhenNothingFollows()
    {
        var (w, stopped) = Feed(page: 2, pageSize: 3, available: 6);

        Assert.Equal(new[] { 4, 5, 6 }, w.Items);
        Assert.False(w.HasMore);
        Assert.False(stopped);
    }

    [Fact]
    public void PageBeyondEnd_IsEmpty()
    {
        var (w, _) = Feed(page: 5, pageSize: 3, available: 6);

        Assert.Empty(w.Items);
        Assert.False(w.HasMore);
    }

    [Fact]
    public void Constructor_ClampsPageAndPageSize()
    {
        var w = new PageWindow<int>(page: 0, pageSize: 5000, maxPageSize: 999);
        Assert.Equal(1, w.Page);
        Assert.Equal(999, w.PageSize);

        var w2 = new PageWindow<int>(page: 2, pageSize: 0, maxPageSize: 999);
        Assert.Equal(1, w2.PageSize);
    }

    [Fact]
    public void ToResult_CarriesItemsAndHasMore()
    {
        var (w, _) = Feed(page: 1, pageSize: 2, available: 3);

        var result = w.ToResult();

        Assert.Equal(new[] { 1, 2 }, result.Items);
        Assert.True(result.HasMore);
        Assert.Null(result.Total);
    }
}
