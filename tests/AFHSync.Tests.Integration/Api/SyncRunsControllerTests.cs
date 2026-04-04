using System.Net;
using System.Net.Http.Json;
using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>
/// Integration tests for SyncRunsController endpoints.
/// Verifies sync trigger with concurrent run prevention (SCHD-05),
/// paginated run listing, detail retrieval, and item filtering.
/// </summary>
[Trait("Category", "Integration")]
public class SyncRunsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SyncRunsControllerTests(TestWebApplicationFactory factory)
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

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string url)
    {
        var cookie = await GetAuthCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task PostSync_ReturnsRunId_WhenNoRunning()
    {
        // Ensure no running sync runs exist
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var runningRuns = db.SyncRuns.Where(r => r.Status == SyncStatus.Running).ToList();
        db.SyncRuns.RemoveRange(runningRuns);
        await db.SaveChangesAsync();

        var response = await AuthenticatedPostAsync("/api/sync-runs", new
        {
            runType = "manual",
            isDryRun = false,
            tunnelIds = (int[]?)null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.TryGetProperty("runId", out var runIdProp));
        Assert.True(runIdProp.GetInt32() > 0);
    }

    [Fact]
    public async Task PostSync_Returns409_WhenRunAlreadyInProgress()
    {
        // Seed a running sync run
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();

        db.SyncRuns.Add(new SyncRun
        {
            RunType = RunType.Manual,
            Status = SyncStatus.Running,
            IsDryRun = false,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var response = await AuthenticatedPostAsync("/api/sync-runs", new
        {
            runType = "manual",
            isDryRun = false
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("already in progress", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetRuns_ReturnsPaginatedList()
    {
        // Seed some sync runs
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();

        for (int i = 0; i < 3; i++)
        {
            db.SyncRuns.Add(new SyncRun
            {
                RunType = RunType.Manual,
                Status = SyncStatus.Success,
                IsDryRun = false,
                StartedAt = DateTime.UtcNow.AddMinutes(-i * 10),
                CompletedAt = DateTime.UtcNow.AddMinutes(-i * 10 + 5),
                CreatedAt = DateTime.UtcNow.AddMinutes(-i * 10)
            });
        }
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync("/api/sync-runs?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var runs = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.NotNull(runs);
        Assert.True(runs.Count >= 3);
    }

    [Fact]
    public async Task GetRun_Returns404_ForNonexistentRun()
    {
        var response = await AuthenticatedGetAsync("/api/sync-runs/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
