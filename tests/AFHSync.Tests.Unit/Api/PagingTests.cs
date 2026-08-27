using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class PagingTests
{
    [Theory]
    [InlineData(0, 0, 20, 100, 1, 20)]      // below range ⇒ page 1, default size
    [InlineData(-5, -1, 50, 200, 1, 50)]
    [InlineData(3, 25, 20, 100, 3, 25)]     // in range ⇒ unchanged
    [InlineData(2, 5000, 20, 500, 2, 500)]  // above max ⇒ max
    [InlineData(1, 1, 20, 100, 1, 1)]       // lower bound is 1
    public void Clamp_NormalisesPageAndPageSize(int page, int pageSize, int defaultSize, int max, int expectedPage, int expectedSize)
    {
        var (p, s) = Paging.Clamp(page, pageSize, defaultSize, max);

        Assert.Equal(expectedPage, p);
        Assert.Equal(expectedSize, s);
    }
}
