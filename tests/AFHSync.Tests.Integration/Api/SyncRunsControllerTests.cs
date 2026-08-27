using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
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
        // Ensure no running or pending sync runs exist. Pending (not just Running) must be
        // cleared too — the test suite shares one Postgres database, and Phase 2 (§2.7) leaves
        // newly-created rows Pending until the enqueued job claims them, so a sibling test's
        // row can still be Pending when this test's guard runs.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var blockingRuns = db.SyncRuns.Where(r => r.Status == SyncStatus.Running || r.Status == SyncStatus.Pending).ToList();
        db.SyncRuns.RemoveRange(blockingRuns);
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
    public async Task PostSync_StoresRequestedTunnelIds_AndExactlyOneJobId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        db.SyncRuns.RemoveRange(db.SyncRuns.Where(r => r.Status == SyncStatus.Running || r.Status == SyncStatus.Pending));
        await db.SaveChangesAsync();

        var response = await AuthenticatedPostAsync("/api/sync-runs", new
        {
            runType = "dry_run",
            isDryRun = true,
            tunnelIds = new[] { 3, 5 }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var runId = body.GetProperty("runId").GetInt32();

        var run = await db.SyncRuns.FindAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Pending, run!.Status);
        Assert.True(run.IsDryRun);
        Assert.Equal(RunType.DryRun, run.RunType);
        Assert.Equal("[3,5]", run.RequestedTunnelIds);
        Assert.False(string.IsNullOrEmpty(run.HangfireJobIds));
        Assert.DoesNotContain(",", run.HangfireJobIds);   // one job, not one per tunnel
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

    [Fact]
    public async Task GetRun_BuildsTunnelSummariesFromRecords_PhotosAndErrorsStillFromItems()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Rec Tunnel", StalePolicy = StalePolicy.FlagHold, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        var run = new SyncRun { RunType = RunType.Manual, Status = SyncStatus.Warning, StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        db.SyncRunTunnels.Add(new SyncRunTunnel
        {
            SyncRunId = run.Id, TunnelId = tunnel.Id, TunnelName = "Rec Tunnel", Status = SyncStatus.Warning,
            TargetsCount = 12, ContactsCreated = 3, ContactsUpdated = 2, ContactsRemoved = 0, ContactsSkipped = 40, ContactsFailed = 1,
            ErrorSummary = "Rec Tunnel: something", StartedAt = DateTime.UtcNow.AddMinutes(-4), CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        db.SyncRunItems.AddRange(
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "failed", ErrorMessage = "Folder 'Rec Tunnel': boom", CreatedAt = DateTime.UtcNow },
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "photo_updated", CreatedAt = DateTime.UtcNow },
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "created", CreatedAt = DateTime.UtcNow });  // one item, but the record says 3
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/sync-runs/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var summary = Assert.Single(body.GetProperty("tunnelSummaries").EnumerateArray());
        Assert.Equal("Rec Tunnel", summary.GetProperty("tunnelName").GetString());
        Assert.Equal(3, summary.GetProperty("contactsCreated").GetInt32());     // from the record, not the single item
        Assert.Equal(1, summary.GetProperty("contactsFailed").GetInt32());
        Assert.Equal(40, summary.GetProperty("contactsSkipped").GetInt32());
        Assert.Equal(1, summary.GetProperty("photosUpdated").GetInt32());       // from items
        Assert.Equal("warning", summary.GetProperty("status").GetString());
        Assert.Equal(12, summary.GetProperty("targetsCount").GetInt32());
        var errors = summary.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Folder 'Rec Tunnel': boom", errors);
    }

    [Fact]
    public async Task GetRun_WithoutRecords_FallsBackToGroupingItems()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Legacy Tunnel", StalePolicy = StalePolicy.FlagHold, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        var run = new SyncRun { RunType = RunType.PhotoSync, Status = SyncStatus.Success, StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        db.SyncRunItems.AddRange(
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "created", CreatedAt = DateTime.UtcNow },
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "created", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/sync-runs/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var summary = Assert.Single(body.GetProperty("tunnelSummaries").EnumerateArray());
        Assert.Equal("Legacy Tunnel", summary.GetProperty("tunnelName").GetString());
        Assert.Equal(2, summary.GetProperty("contactsCreated").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, summary.GetProperty("status").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, summary.GetProperty("targetsCount").ValueKind);
    }
}
