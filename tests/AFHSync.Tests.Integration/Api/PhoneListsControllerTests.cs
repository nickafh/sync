using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>
/// Integration tests for PhoneListsController endpoints.
/// Verifies list, detail, and 404 handling.
/// </summary>
[Trait("Category", "Integration")]
public class PhoneListsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PhoneListsControllerTests(TestWebApplicationFactory factory)
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

    private void SeedPhoneLists(int count = 2)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();

        // Clear any existing phone lists
        db.PhoneLists.RemoveRange(db.PhoneLists.ToList());
        db.SaveChanges();

        for (int i = 1; i <= count; i++)
        {
            db.PhoneLists.Add(new PhoneList
            {
                Name = $"All Contacts {i}",
                Description = $"Phone list {i}",
                ContactCount = i * 10,
                UserCount = i * 5,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task GetAll_ReturnsPhoneLists()
    {
        SeedPhoneLists(2);
        var response = await AuthenticatedGetAsync("/api/phone-lists");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var lists = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.NotNull(lists);
        Assert.True(lists.Count >= 2);
    }

    [Fact]
    public async Task GetAll_RequiresAuthentication()
    {
        var response = await _client.GetAsync("/api/phone-lists");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_ExistingList_Returns200WithDetail()
    {
        // Seed a phone list
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var list = new PhoneList
        {
            Name = "Specific List",
            Description = "A specific phone list for testing",
            ContactCount = 50,
            UserCount = 25,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.PhoneLists.Add(list);
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Specific List", body.GetProperty("name").GetString());
        // contactCount is now computed from ContactSyncState, not the static field
        Assert.Equal(0, body.GetProperty("contactCount").GetInt32());
    }

    [Fact]
    public async Task GetById_NonExistentList_Returns404()
    {
        var response = await AuthenticatedGetAsync("/api/phone-lists/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContacts_NonExistentList_Returns404()
    {
        var response = await AuthenticatedGetAsync("/api/phone-lists/99999/contacts");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContacts_ExistingList_ReturnsEmptyEnvelopeWithTotal()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var list = new PhoneList { Name = "Empty List", ContactCount = 0, UserCount = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.PhoneLists.Add(list);
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetContacts_PagesDistinctSourceUsers_WithTotal_AndDefaultsBadPageSize()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var list = new PhoneList { Name = "Paged List", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.PhoneLists.Add(list);
        var mailbox = new TargetMailbox { EntraId = "pl-mbx", Email = "pl-mbx@contoso.com", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.TargetMailboxes.Add(mailbox);
        var users = new[]
        {
            new SourceUser { EntraId = "pl-u1", DisplayName = "Cara", Email = "cara@contoso.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new SourceUser { EntraId = "pl-u2", DisplayName = "Abe", Email = "abe@contoso.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new SourceUser { EntraId = "pl-u3", DisplayName = "Bea", Email = "bea@contoso.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };
        db.SourceUsers.AddRange(users);
        await db.SaveChangesAsync();
        foreach (var u in users)
            db.ContactSyncStates.Add(new ContactSyncState { SourceUserId = u.Id, PhoneListId = list.Id, TargetMailboxId = mailbox.Id, GraphContactId = $"g-{u.Id}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        // A second state row for the same user must not count twice.
        db.ContactSyncStates.Add(new ContactSyncState { SourceUserId = users[0].Id, PhoneListId = list.Id, TargetMailboxId = mailbox.Id, GraphContactId = "g-dup", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var first = await (await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts?page=1&pageSize=2")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var second = await (await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts?page=2&pageSize=2")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var defaulted = await (await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts?page=1&pageSize=0")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, defaulted.GetProperty("items").GetArrayLength());    // pageSize 0 ⇒ default 20
        Assert.False(defaulted.GetProperty("hasMore").GetBoolean());
    }
}
