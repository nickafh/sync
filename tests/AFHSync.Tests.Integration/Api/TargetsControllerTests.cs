using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>Phase 2 (§2.1): GET /api/targets/unavailable lists stamped mailboxes with an "N of M" header.</summary>
[Trait("Category", "Integration")]
public class TargetsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TargetsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string url)
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        loginResponse.EnsureSuccessStatusCode();
        var cookie = loginResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task GetUnavailable_ListsStampedMailboxes_OldestFirst_WithTotals()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var now = DateTime.UtcNow;
        db.TargetMailboxes.AddRange(
            new TargetMailbox { EntraId = "t6-ok", Email = "t6-ok@contoso.com", DisplayName = "OK", IsActive = true, CreatedAt = now, UpdatedAt = now },
            new TargetMailbox { EntraId = "t6-newer", Email = "t6-newer@contoso.com", DisplayName = "Newer", IsActive = true, CreatedAt = now, UpdatedAt = now,
                MailboxUnavailableAt = now.AddDays(-1), MailboxLastProbedAt = now.AddDays(-1), MailboxUnavailableReason = "soft-deleted" },
            new TargetMailbox { EntraId = "t6-older", Email = "t6-older@contoso.com", DisplayName = "Older", IsActive = true, CreatedAt = now, UpdatedAt = now,
                MailboxUnavailableAt = now.AddDays(-10), MailboxLastProbedAt = now.AddDays(-3), MailboxUnavailableReason = "on-prem" },
            new TargetMailbox { EntraId = "t6-inactive", Email = "t6-inactive@contoso.com", DisplayName = "Gone", IsActive = false, CreatedAt = now, UpdatedAt = now,
                MailboxUnavailableAt = now.AddDays(-20), MailboxLastProbedAt = now.AddDays(-20), MailboxUnavailableReason = "deleted" });
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync("/api/targets/unavailable");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        var emails = items.Select(i => i.GetProperty("email").GetString()).ToList();
        Assert.Contains("t6-older@contoso.com", emails);
        Assert.Contains("t6-newer@contoso.com", emails);
        Assert.DoesNotContain("t6-ok@contoso.com", emails);
        Assert.DoesNotContain("t6-inactive@contoso.com", emails);        // inactive rows are not target mailboxes
        Assert.True(emails.IndexOf("t6-older@contoso.com") < emails.IndexOf("t6-newer@contoso.com"));  // oldest first
        Assert.Equal(items.Count, body.GetProperty("unavailable").GetInt32());
        Assert.True(body.GetProperty("totalActive").GetInt32() >= 3);
        var older = items.Single(i => i.GetProperty("email").GetString() == "t6-older@contoso.com");
        Assert.Equal("on-prem", older.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(older.GetProperty("unavailableSince").GetString()));
    }
}
