using System.Net;
using System.Net.Http.Json;
using AFHSync.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AFHSync.Tests.Unit;

/// <summary>
/// Tests for global auth filter behavior (AUTH-03).
/// Validates that unauthenticated requests are rejected and [AllowAnonymous] endpoints are accessible.
/// Uses WebApplicationFactory from the integration project.
/// </summary>
[Trait("Category", "Unit")]
public class AuthMiddlewareTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthMiddlewareTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Request_WithoutJwt_Returns401()
    {
        // GET /api/auth/me without any cookie should return 401
        var response = await _client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_WithoutJwt_DoesNotReturn401()
    {
        // /health is [AllowAnonymous] -- should not return 401
        var response = await _client.GetAsync("/health");

        // May return 200 or 503 depending on DB state, but never 401
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginEndpoint_WithoutJwt_Returns200()
    {
        // POST /api/auth/login is [AllowAnonymous] -- should accept without auth
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LogoutEndpoint_WithoutJwt_Returns200()
    {
        // POST /api/auth/logout is [AllowAnonymous] -- should accept without auth
        var response = await _client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GraphHealthEndpoint_WithoutJwt_DoesNotReturn401()
    {
        // /health/graph is [AllowAnonymous] -- should not return 401
        var response = await _client.GetAsync("/health/graph");

        // May return 503 (graph not configured in test), but never 401
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
