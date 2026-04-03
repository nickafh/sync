using Xunit;

namespace AFHSync.Tests.Integration;

[Trait("Category", "Integration")]
public class AuthTests
{
    [Fact]
    public void Login_ValidCredentials_Returns200_Placeholder()
    {
        // TODO: implement in Plan 03 — POST /api/auth/login with valid creds
        Assert.True(true, "Stub — replace with real auth test");
    }

    [Fact]
    public void Login_SetsCookie_HttpOnly_Placeholder()
    {
        // TODO: implement in Plan 03 — verify Set-Cookie header has HttpOnly flag
        Assert.True(true, "Stub — replace with real cookie test");
    }

    [Fact]
    public void Cookie_PersistsAcrossRequests_Placeholder()
    {
        // TODO: implement in Plan 03 — send cookie on subsequent request, verify 200
        Assert.True(true, "Stub — replace with real cookie persistence test");
    }
}
