namespace AFHSync.Api.Services;

/// <summary>Phase 3 (§3.3): one place for page/pageSize normalisation.</summary>
public static class Paging
{
    /// <summary>
    /// page &lt; 1 ⇒ 1; pageSize &lt; 1 ⇒ <paramref name="defaultSize"/>; pageSize &gt; <paramref name="max"/> ⇒ max.
    /// </summary>
    public static (int page, int pageSize) Clamp(int page, int pageSize, int defaultSize, int max)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = defaultSize;
        if (pageSize > max) pageSize = max;
        return (page, pageSize);
    }
}
