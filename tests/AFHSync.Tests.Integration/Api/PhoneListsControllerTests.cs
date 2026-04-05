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
        Assert.Equal(50, body.GetProperty("contactCount").GetInt32());
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
    public async Task GetContacts_ExistingList_Returns200WithEmptyArray()
    {
        // Seed a phone list with no contacts
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var list = new PhoneList
        {
            Name = "Empty List",
            ContactCount = 0,
            UserCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.PhoneLists.Add(list);
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contacts = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.NotNull(contacts);
        Assert.Empty(contacts);
    }
}
