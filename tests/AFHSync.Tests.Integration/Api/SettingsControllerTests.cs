using System.Net;
using System.Net.Http.Json;
using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>
/// Integration tests for SettingsController endpoints.
/// Verifies GET returns seed settings, PUT updates values,
/// and PUT returns 404 for unknown keys.
/// </summary>
[Trait("Category", "Integration")]
public class SettingsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SettingsControllerTests(TestWebApplicationFactory factory)
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

    private void SeedSettings()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();

        // Only seed if no settings exist (InMemory DB doesn't run EF seed data)
        if (!db.AppSettings.Any())
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = "sync_schedule_cron", Value = "0 */4 * * *", Description = "Sync runs every 4 hours" },
                new AppSetting { Key = "photo_sync_mode", Value = "included", Description = "included | separate_pass | disabled" },
                new AppSetting { Key = "batch_size", Value = "50", Description = "Contacts per batch for Graph writes" },
                new AppSetting { Key = "parallelism", Value = "4", Description = "Concurrent target mailbox processing" }
            );
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task GetSettings_ReturnsSeedSettings()
    {
        SeedSettings();
        var response = await AuthenticatedGetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.NotNull(settings);
        Assert.True(settings.Count >= 4);

        // Verify sync_schedule_cron is present
        Assert.Contains(settings, s =>
            s.TryGetProperty("key", out var key) && key.GetString() == "sync_schedule_cron");
    }

    [Fact]
    public async Task PutSettings_UpdatesValue()
    {
        SeedSettings();
        var response = await AuthenticatedPutAsync("/api/settings", new
        {
            settings = new[]
            {
                new { key = "batch_size", value = "100" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify the value was updated in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var setting = db.AppSettings.FirstOrDefault(s => s.Key == "batch_size");
        Assert.NotNull(setting);
        Assert.Equal("100", setting.Value);
    }

    [Fact]
    public async Task PutSettings_UnknownKey_Returns404()
    {
        SeedSettings();
        var response = await AuthenticatedPutAsync("/api/settings", new
        {
            settings = new[]
            {
                new { key = "nonexistent_key", value = "whatever" }
            }
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("not found", body.GetProperty("message").GetString());
    }
}
