using System.Net;
using System.Net.Http.Json;
using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>
/// Integration tests for TunnelsController endpoints.
/// Verifies full tunnel CRUD: list, detail, create, status update, delete.
/// Tests DDG-04: SourceIdentifier (Graph filter) and SourceDisplayName stored on create.
/// </summary>
[Trait("Category", "Integration")]
public class TunnelsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TunnelsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private async Task<string> GetAuthCookieAsync()
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin" });
        loginResponse.EnsureSuccessStatusCode();
        var setCookie = loginResponse.Headers.GetValues("Set-Cookie").First();
        return setCookie.Split(';')[0]; // "afh_auth=<jwt>"
    }

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string url)
    {
        var cookie = await GetAuthCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> AuthenticatedPostAsync<T>(string url, T body)
    {
        var cookie = await GetAuthCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> AuthenticatedPutAsync<T>(string url, T body)
    {
        var cookie = await GetAuthCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> AuthenticatedDeleteAsync(string url)
    {
        var cookie = await GetAuthCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    private void SeedTunnels(int count = 2)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();

        // Clear any existing tunnels
        db.Tunnels.RemoveRange(db.Tunnels.ToList());
        db.SaveChanges();

        for (int i = 1; i <= count; i++)
        {
            db.Tunnels.Add(new Tunnel
            {
                Name = $"Test Tunnel {i}",
                SourceType = SourceType.Ddg,
                SourceIdentifier = $"startsWith(displayName, 'Office{i}')",
                SourceDisplayName = $"Office {i} DDG",
                SourceSmtpAddress = $"office{i}-ddg@atlantafinehomes.com",
                TargetScope = TargetScope.AllUsers,
                StalePolicy = StalePolicy.FlagHold,
                StaleHoldDays = 14,
                Status = TunnelStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task GetAll_ReturnsTunnels()
    {
        SeedTunnels(2);
        var response = await AuthenticatedGetAsync("/api/tunnels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tunnels = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.NotNull(tunnels);
        Assert.True(tunnels.Count >= 2);
    }

    [Fact]
    public async Task GetAll_RequiresAuthentication()
    {
        var response = await _client.GetAsync("/api/tunnels");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostTunnel_CreatesTunnel_Returns201WithId()
    {
        // Create a phone list first to use as target
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var phoneList = new PhoneList
        {
            Name = "All Contacts",
            ContactCount = 0,
            UserCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.PhoneLists.Add(phoneList);
        await db.SaveChangesAsync();

        var createRequest = new
        {
            name = "Buckhead Office",
            sourceType = "Ddg",
            sourceIdentifier = "startsWith(displayName, 'Buckhead')",
            sourceDisplayName = "Buckhead DDG",
            sourceSmtpAddress = "buckhead-ddg@atlantafinehomes.com",
            targetScope = "AllUsers",
            targetListIds = new[] { phoneList.Id },
            fieldProfileId = (int?)null,
            stalePolicy = "FlagHold",
            staleDays = 14
        };

        var response = await AuthenticatedPostAsync("/api/tunnels", createRequest);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.TryGetProperty("id", out var idProp));
        Assert.True(idProp.GetInt32() > 0);
    }

    [Fact]
    public async Task PostTunnel_StoresDdgReference_DDG04()
    {
        // Per DDG-04: SourceIdentifier stores the Graph $filter and SourceDisplayName stores DDG name
        var createRequest = new
        {
            name = "Intown Office",
            sourceType = "Ddg",
            sourceIdentifier = "startsWith(displayName, 'Intown')",
            sourceDisplayName = "Intown DDG Display Name",
            sourceSmtpAddress = "intown-ddg@atlantafinehomes.com",
            targetScope = "AllUsers",
            targetListIds = Array.Empty<int>(),
            fieldProfileId = (int?)null,
            stalePolicy = "FlagHold",
            staleDays = 14
        };

        var createResponse = await AuthenticatedPostAsync("/api/tunnels", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var body = await createResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var newId = body.GetProperty("id").GetInt32();

        // Fetch the tunnel to verify DDG-04 fields were stored
        var getResponse = await AuthenticatedGetAsync($"/api/tunnels/{newId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var tunnel = await getResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("startsWith(displayName, 'Intown')", tunnel.GetProperty("sourceIdentifier").GetString());
        Assert.Equal("Intown DDG Display Name", tunnel.GetProperty("sourceDisplayName").GetString());
        Assert.Equal("intown-ddg@atlantafinehomes.com", tunnel.GetProperty("sourceSmtpAddress").GetString());
    }

    [Fact]
    public async Task PutTunnelStatus_TogglesStatus_Returns200()
    {
        // Seed a tunnel
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel
        {
            Name = "Status Toggle Test",
            SourceType = SourceType.Ddg,
            SourceIdentifier = "filter",
            TargetScope = TargetScope.AllUsers,
            StalePolicy = StalePolicy.FlagHold,
            StaleHoldDays = 14,
            Status = TunnelStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Tunnels.Add(tunnel);
        await db.SaveChangesAsync();

        var response = await AuthenticatedPutAsync($"/api/tunnels/{tunnel.Id}/status",
            new { status = "Inactive" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify in DB
        db.ChangeTracker.Clear();
        var updated = await db.Tunnels.FindAsync(tunnel.Id);
        Assert.Equal(TunnelStatus.Inactive, updated!.Status);
    }

    [Fact]
    public async Task DeleteTunnel_WithoutSyncState_Returns204()
    {
        // Seed a tunnel with no ContactSyncState
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel
        {
            Name = "Delete Me",
            SourceType = SourceType.Ddg,
            SourceIdentifier = "filter",
            TargetScope = TargetScope.AllUsers,
            StalePolicy = StalePolicy.FlagHold,
            StaleHoldDays = 14,
            Status = TunnelStatus.Inactive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Tunnels.Add(tunnel);
        await db.SaveChangesAsync();

        var response = await AuthenticatedDeleteAsync($"/api/tunnels/{tunnel.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetTunnel_NotFound_Returns404()
    {
        var response = await AuthenticatedGetAsync("/api/tunnels/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
