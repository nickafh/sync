using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

[Trait("Category", "Integration")]
public class ExportsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ExportsControllerTests(TestWebApplicationFactory factory)
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
        return setCookie.Split(';')[0];
    }

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string url)
    {
        var cookie = await GetAuthCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task GetContactsXlsx_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/exports/contacts.xlsx");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetContactsXlsx_ReturnsXlsxWithExpectedHeaders()
    {
        // Arrange — seed one tunnel with one live contact
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
            db.Tunnels.Add(new Tunnel { Id = 9001, Name = "ExportTest", Status = TunnelStatus.Active });
            db.SourceUsers.Add(new SourceUser
            {
                Id = 9001,
                EntraId = "entra-9001",
                DisplayName = "Export Tester",
                Email = "tester@afh.com",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.ContactSyncStates.Add(new ContactSyncState
            {
                SourceUserId = 9001,
                TunnelId = 9001,
                PhoneListId = 1,
                TargetMailboxId = 1,
                IsStale = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await AuthenticatedGetAsync("/api/exports/contacts.xlsx");

        // Assert — HTTP shape
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.StartsWith("afh-sync-contacts-", disposition.FileName?.Trim('"'));
        Assert.EndsWith(".xlsx", disposition.FileName?.Trim('"'));

        // Assert — file contents
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheet("ExportTest");
        Assert.Equal("Export Tester", sheet.Cell(2, 1).GetString());
    }
}
