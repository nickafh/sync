using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>Phase 3 (§3.5): the exclusion replace is atomic and de-duplicated.</summary>
[Trait("Category", "Integration")]
public class ContactExclusionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ContactExclusionsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<string> GetAuthCookieAsync()
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        loginResponse.EnsureSuccessStatusCode();
        return loginResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Cookie", await GetAuthCookieAsync());
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task PutExclusions_DedupesByEntraIdCaseInsensitively_AndReplacesPreviousSet()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Exclusions Tunnel", StalePolicy = StalePolicy.FlagHold, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        await db.SaveChangesAsync();

        var first = await SendAsync(HttpMethod.Put, $"/api/tunnels/{tunnel.Id}/contact-exclusions", new
        {
            exclusions = new[]
            {
                new { entraId = "AAA-1", displayName = "Alice", email = "alice@contoso.com" },
                new { entraId = "aaa-1", displayName = "Alice again", email = "alice@contoso.com" },   // duplicate, different case
                new { entraId = "BBB-2", displayName = "Bob", email = "bob@contoso.com" },
                new { entraId = "", displayName = "Blank", email = (string?)null },                    // dropped
            }
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Saved 2 exclusion(s).", firstBody.GetProperty("message").GetString());

        var afterFirst = await (await SendAsync(HttpMethod.Get, $"/api/tunnels/{tunnel.Id}/contact-exclusions")).Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.Equal(2, afterFirst!.Count);

        var second = await SendAsync(HttpMethod.Put, $"/api/tunnels/{tunnel.Id}/contact-exclusions", new
        {
            exclusions = new[] { new { entraId = "CCC-3", displayName = "Cara", email = "cara@contoso.com" } }
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var afterSecond = await (await SendAsync(HttpMethod.Get, $"/api/tunnels/{tunnel.Id}/contact-exclusions")).Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        var only = Assert.Single(afterSecond!);
        Assert.Equal("CCC-3", only.GetProperty("entraId").GetString());
    }

    [Fact]
    public async Task PutExclusions_UnknownTunnel_Returns404()
    {
        var response = await SendAsync(HttpMethod.Put, "/api/tunnels/99999/contact-exclusions", new { exclusions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
