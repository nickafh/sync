using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AFHSync.Tests.Integration;

/// <summary>
/// Integration tests for JWT authentication (AUTH-01, AUTH-02, AUTH-04).
/// Uses WebApplicationFactory with InMemory database.
/// Validates login endpoint, cookie behavior, and session persistence.
/// </summary>
[Trait("Category", "Integration")]
public class AuthTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200()
    {
        // appsettings.Development.json has admin/admin
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "wrong", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_SetsCookie_HttpOnly()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        var setCookie = response.Headers.GetValues("Set-Cookie").FirstOrDefault();
        Assert.NotNull(setCookie);
        Assert.Contains("afh_auth=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_SetsCookie_SameSiteStrict()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        var setCookie = response.Headers.GetValues("Set-Cookie").FirstOrDefault();
        Assert.NotNull(setCookie);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_PersistsAcrossRequests()
    {
        // Login to get the cookie
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        // Extract cookie from Set-Cookie header
        var setCookie = loginResponse.Headers.GetValues("Set-Cookie").First();
        var cookieValue = setCookie.Split(';')[0]; // "afh_auth=<jwt>"

        // Send request to /api/auth/me with the cookie
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookieValue);
        var meResponse = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsUsername_WhenAuthenticated()
    {
        // Login to get the cookie
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        var setCookie = loginResponse.Headers.GetValues("Set-Cookie").First();
        var cookieValue = setCookie.Split(';')[0];

        // Hit /api/auth/me with the cookie
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookieValue);
        var meResponse = await _client.SendAsync(request);

        var body = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal("admin", body?.Username);
    }

    [Fact]
    public async Task Logout_ClearsCookie()
    {
        // Login first
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });

        var setCookie = loginResponse.Headers.GetValues("Set-Cookie").First();
        var cookieValue = setCookie.Split(';')[0];

        // Logout
        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("Cookie", cookieValue);
        var logoutResponse = await _client.SendAsync(logoutRequest);

        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        // Verify Set-Cookie header clears the cookie (expires in the past)
        var logoutCookie = logoutResponse.Headers.GetValues("Set-Cookie").FirstOrDefault();
        Assert.NotNull(logoutCookie);
        Assert.Contains("afh_auth=", logoutCookie);
    }

    private record MeResponse(string? Username);
}
