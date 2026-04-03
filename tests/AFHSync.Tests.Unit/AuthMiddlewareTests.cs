using Xunit;

namespace AFHSync.Tests.Unit;

[Trait("Category", "Unit")]
public class AuthMiddlewareTests
{
    [Fact]
    public void Request_WithoutJwt_Returns401_Placeholder()
    {
        // TODO: implement in Plan 03 — verify global auth filter rejects unauthenticated requests
        Assert.True(true, "Stub — replace with real middleware test");
    }

    [Fact]
    public void HealthEndpoint_WithoutJwt_Returns200_Placeholder()
    {
        // TODO: implement in Plan 03 — verify [AllowAnonymous] on health endpoint works
        Assert.True(true, "Stub — replace with real middleware test");
    }
}
