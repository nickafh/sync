# Contact Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Export" sidebar entry and a `/exports` page that downloads an `.xlsx` workbook with one sheet per tunnel listing the live (non-stale) contacts being synced through it.

**Architecture:** New `IContactExportService` builds the workbook with ClosedXML using a single grouped EF query over `ContactSyncState` joined to `SourceUser` and `Tunnel`. New `ExportsController` streams the file as a `FileStreamResult` from `GET /api/exports/contacts.xlsx`. Frontend adds a sidebar entry and a small page with a "Download .xlsx" button that fetches the blob and triggers a browser download.

**Tech Stack:** ASP.NET Core 10 controller + service, EF Core 10 with InMemory tests, ClosedXML 0.105.* for `.xlsx` generation, Next.js 15 App Router page using `fetch` + blob download, xUnit + `Microsoft.AspNetCore.Mvc.Testing` for tests.

**Spec:** `docs/superpowers/specs/2026-05-08-contact-export-design.md`

**Standing instruction:** The user handles all `git` operations. Do **not** run `git add` / `git commit` / `git push`. Where the task structure says "Commit", just stop and report progress; the user will commit.

---

## File Structure

**New:**
- `api/Services/IContactExportService.cs` — interface contract for the workbook builder
- `api/Services/ContactExportService.cs` — ClosedXML implementation, sheet-name sanitizer
- `api/Controllers/ExportsController.cs` — thin controller streaming the workbook
- `frontend/src/app/(app)/exports/page.tsx` — page with "Download .xlsx" button
- `tests/AFHSync.Tests.Unit/Api/ContactExportServiceTests.cs` — unit tests over InMemory DB
- `tests/AFHSync.Tests.Integration/Api/ExportsControllerTests.cs` — endpoint integration test

**Modified:**
- `api/AFHSync.Api.csproj` — add ClosedXML package
- `api/Program.cs` — register `IContactExportService`
- `frontend/src/components/layout/Sidebar.tsx` — add "Export" nav entry

---

## Task 1: Add ClosedXML dependency

**Files:**
- Modify: `api/AFHSync.Api.csproj`

- [ ] **Step 1: Add the package reference**

Open `api/AFHSync.Api.csproj` and inside the existing `<ItemGroup>` that contains the other `<PackageReference>` entries, add:

```xml
<PackageReference Include="ClosedXML" Version="0.105.*" />
```

Place it alphabetically between `BCrypt.Net-Next` and `Hangfire.AspNetCore`.

- [ ] **Step 2: Restore and verify the build**

Run from repo root:
```
dotnet restore api/AFHSync.Api.csproj
dotnet build api/AFHSync.Api.csproj
```

Expected: build succeeds, no errors. The restore output should mention `ClosedXML 0.105.x` being installed.

- [ ] **Step 3: Stop for user commit**

Tell the user: "Task 1 complete: ClosedXML added. Ready for you to commit."

---

## Task 2: Define `IContactExportService` interface

**Files:**
- Create: `api/Services/IContactExportService.cs`

- [ ] **Step 1: Create the interface file**

Create `api/Services/IContactExportService.cs` with:

```csharp
namespace AFHSync.Api.Services;

/// <summary>
/// Builds an Excel workbook (.xlsx bytes) listing the live (non-stale) source
/// contacts currently being synced, with one sheet per tunnel.
/// </summary>
public interface IContactExportService
{
    Task<byte[]> BuildContactsWorkbookAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Verify the build**

Run:
```
dotnet build api/AFHSync.Api.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Stop for user commit**

Tell the user: "Task 2 complete: `IContactExportService` defined."

---

## Task 3: Write the failing service tests

**Files:**
- Create: `tests/AFHSync.Tests.Unit/Api/ContactExportServiceTests.cs`

- [ ] **Step 1: Add the test file**

Create `tests/AFHSync.Tests.Unit/Api/ContactExportServiceTests.cs` with these tests. They use the EF Core InMemory provider, seed two tunnels, and assert against the workbook structure by reading it back with ClosedXML.

```csharp
using AFHSync.Api.Services;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AFHSync.Tests.Unit.Api;

public class ContactExportServiceTests
{
    private static AFHSyncDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AFHSyncDbContext(options);
    }

    private static SourceUser MakeUser(int id, string display, string email)
        => new()
        {
            Id = id,
            EntraId = $"entra-{id}",
            DisplayName = display,
            FirstName = display.Split(' ')[0],
            LastName = display.Split(' ').Length > 1 ? display.Split(' ')[1] : null,
            Email = email,
            JobTitle = "Agent",
            Department = "Sales",
            OfficeLocation = "Buckhead",
            BusinessPhone = "404-555-0100",
            MobilePhone = "404-555-0101",
            CompanyName = "AFH",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static ContactSyncState MakeState(int sourceUserId, int tunnelId, bool stale = false)
        => new()
        {
            SourceUserId = sourceUserId,
            PhoneListId = 1,
            TargetMailboxId = 1,
            TunnelId = tunnelId,
            IsStale = stale,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Fact]
    public async Task BuildContactsWorkbookAsync_CreatesOneSheetPerTunnel()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Buckhead", Status = TunnelStatus.Active });
        db.Tunnels.Add(new Tunnel { Id = 2, Name = "Intown", Status = TunnelStatus.Active });
        db.SourceUsers.Add(MakeUser(10, "Alice Adams", "alice@afh.com"));
        db.SourceUsers.Add(MakeUser(11, "Bob Brown", "bob@afh.com"));
        db.ContactSyncStates.Add(MakeState(10, 1));
        db.ContactSyncStates.Add(MakeState(11, 2));
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        Assert.Equal(2, wb.Worksheets.Count);
        Assert.Contains(wb.Worksheets, w => w.Name == "Buckhead");
        Assert.Contains(wb.Worksheets, w => w.Name == "Intown");
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_ExcludesStaleContacts()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Buckhead", Status = TunnelStatus.Active });
        db.SourceUsers.Add(MakeUser(10, "Alice Adams", "alice@afh.com"));
        db.SourceUsers.Add(MakeUser(11, "Stale Steve", "steve@afh.com"));
        db.ContactSyncStates.Add(MakeState(10, 1, stale: false));
        db.ContactSyncStates.Add(MakeState(11, 1, stale: true));
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheet("Buckhead");
        // Header row + 1 contact row = 2 used rows
        Assert.Equal(2, sheet.RangeUsed()!.RowCount());
        Assert.Equal("Alice Adams", sheet.Cell(2, 1).GetString());
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_HasExpectedHeaders()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Buckhead", Status = TunnelStatus.Active });
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheet("Buckhead");
        var headers = new[]
        {
            "Display Name", "First Name", "Last Name", "Email",
            "Job Title", "Department", "Office",
            "Business Phone", "Mobile Phone", "Company"
        };
        for (var i = 0; i < headers.Length; i++)
        {
            Assert.Equal(headers[i], sheet.Cell(1, i + 1).GetString());
        }
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_DeduplicatesContactsWithinTunnel()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Buckhead", Status = TunnelStatus.Active });
        db.SourceUsers.Add(MakeUser(10, "Alice Adams", "alice@afh.com"));
        // Same source user, same tunnel, two different mailboxes/lists
        db.ContactSyncStates.Add(new ContactSyncState
        {
            SourceUserId = 10, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
            IsStale = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        db.ContactSyncStates.Add(new ContactSyncState
        {
            SourceUserId = 10, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 2,
            IsStale = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheet("Buckhead");
        // Header + 1 unique contact row = 2
        Assert.Equal(2, sheet.RangeUsed()!.RowCount());
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_AppearsInBothTunnelsWhenShared()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Buckhead", Status = TunnelStatus.Active });
        db.Tunnels.Add(new Tunnel { Id = 2, Name = "Intown", Status = TunnelStatus.Active });
        db.SourceUsers.Add(MakeUser(10, "Alice Adams", "alice@afh.com"));
        db.ContactSyncStates.Add(MakeState(10, 1));
        db.ContactSyncStates.Add(MakeState(10, 2));
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        Assert.Equal("Alice Adams", wb.Worksheet("Buckhead").Cell(2, 1).GetString());
        Assert.Equal("Alice Adams", wb.Worksheet("Intown").Cell(2, 1).GetString());
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_SanitizesInvalidSheetNameCharacters()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Sales / Buckhead [VIP]", Status = TunnelStatus.Active });
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        // The forbidden characters / [ ] are replaced with -
        Assert.Equal("Sales - Buckhead -VIP-", wb.Worksheet(1).Name);
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_TruncatesSheetNamesOver31Chars()
    {
        using var db = NewDb();
        var longName = new string('A', 50);
        db.Tunnels.Add(new Tunnel { Id = 1, Name = longName, Status = TunnelStatus.Active });
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        Assert.Equal(31, wb.Worksheet(1).Name.Length);
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_DisambiguatesDuplicateSheetNamesAfterSanitization()
    {
        using var db = NewDb();
        // Both tunnel names sanitize and truncate to the same value
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Sales/Team", Status = TunnelStatus.Active });
        db.Tunnels.Add(new Tunnel { Id = 2, Name = "Sales\\Team", Status = TunnelStatus.Active });
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        Assert.Equal(2, wb.Worksheets.Count);
        // Sheet names must be unique
        Assert.NotEqual(wb.Worksheet(1).Name, wb.Worksheet(2).Name);
    }

    [Fact]
    public async Task BuildContactsWorkbookAsync_EmptyTunnelStillGetsSheet()
    {
        using var db = NewDb();
        db.Tunnels.Add(new Tunnel { Id = 1, Name = "Empty", Status = TunnelStatus.Active });
        await db.SaveChangesAsync();

        var svc = new ContactExportService(db);
        var bytes = await svc.BuildContactsWorkbookAsync(CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheet("Empty");
        // Header row only
        Assert.Equal(1, sheet.RangeUsed()!.RowCount());
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run:
```
dotnet test tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj --filter FullyQualifiedName~ContactExportServiceTests
```

Expected: build error — `ContactExportService` does not exist yet. (We will implement it next.)

- [ ] **Step 3: Stop for user commit**

Tell the user: "Task 3 complete: failing tests written for `ContactExportService`."

---

## Task 4: Implement `ContactExportService`

**Files:**
- Create: `api/Services/ContactExportService.cs`

- [ ] **Step 1: Create the implementation**

Create `api/Services/ContactExportService.cs` with:

```csharp
using System.Text.RegularExpressions;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Services;

public class ContactExportService : IContactExportService
{
    private static readonly string[] HeaderRow =
    {
        "Display Name", "First Name", "Last Name", "Email",
        "Job Title", "Department", "Office",
        "Business Phone", "Mobile Phone", "Company"
    };

    private static readonly Regex InvalidSheetChars = new(@"[:\\/\?\*\[\]]", RegexOptions.Compiled);

    private readonly AFHSyncDbContext _db;

    public ContactExportService(AFHSyncDbContext db)
    {
        _db = db;
    }

    public async Task<byte[]> BuildContactsWorkbookAsync(CancellationToken ct)
    {
        var tunnels = await _db.Tunnels
            .OrderBy(t => t.Name)
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        var tunnelIds = tunnels.Select(t => t.Id).ToList();

        // One round-trip: pull every (tunnelId, sourceUser-projection) pair for
        // live sync state, then group in memory. The dataset is small enough
        // that this is simpler than per-tunnel queries.
        var rows = await _db.ContactSyncStates
            .Where(s => s.TunnelId != null
                        && tunnelIds.Contains(s.TunnelId.Value)
                        && !s.IsStale)
            .Select(s => new
            {
                TunnelId = s.TunnelId!.Value,
                User = new ContactRow(
                    s.SourceUser.DisplayName,
                    s.SourceUser.FirstName,
                    s.SourceUser.LastName,
                    s.SourceUser.Email,
                    s.SourceUser.JobTitle,
                    s.SourceUser.Department,
                    s.SourceUser.OfficeLocation,
                    s.SourceUser.BusinessPhone,
                    s.SourceUser.MobilePhone,
                    s.SourceUser.CompanyName,
                    s.SourceUserId)
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var byTunnel = rows
            .GroupBy(r => r.TunnelId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .GroupBy(x => x.User.SourceUserId) // dedupe within tunnel
                    .Select(grp => grp.First().User)
                    .OrderBy(u => u.DisplayName ?? string.Empty)
                    .ToList());

        using var wb = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tunnels)
        {
            ct.ThrowIfCancellationRequested();
            var sheetName = MakeUniqueSheetName(SanitizeSheetName(t.Name), usedNames);
            usedNames.Add(sheetName);

            var sheet = wb.Worksheets.Add(sheetName);

            // Header row
            for (var i = 0; i < HeaderRow.Length; i++)
            {
                sheet.Cell(1, i + 1).Value = HeaderRow[i];
            }
            sheet.Range(1, 1, 1, HeaderRow.Length).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(1);

            if (byTunnel.TryGetValue(t.Id, out var contacts))
            {
                var rowIdx = 2;
                foreach (var c in contacts)
                {
                    sheet.Cell(rowIdx, 1).Value = c.DisplayName ?? string.Empty;
                    sheet.Cell(rowIdx, 2).Value = c.FirstName ?? string.Empty;
                    sheet.Cell(rowIdx, 3).Value = c.LastName ?? string.Empty;
                    sheet.Cell(rowIdx, 4).Value = c.Email ?? string.Empty;
                    sheet.Cell(rowIdx, 5).Value = c.JobTitle ?? string.Empty;
                    sheet.Cell(rowIdx, 6).Value = c.Department ?? string.Empty;
                    sheet.Cell(rowIdx, 7).Value = c.OfficeLocation ?? string.Empty;
                    sheet.Cell(rowIdx, 8).Value = c.BusinessPhone ?? string.Empty;
                    sheet.Cell(rowIdx, 9).Value = c.MobilePhone ?? string.Empty;
                    sheet.Cell(rowIdx, 10).Value = c.CompanyName ?? string.Empty;
                    rowIdx++;
                }
            }

            sheet.Columns().AdjustToContents();
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    internal static string SanitizeSheetName(string name)
    {
        var cleaned = InvalidSheetChars.Replace(name ?? string.Empty, "-");
        if (cleaned.Length > 31) cleaned = cleaned[..31];
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Tunnel";
        return cleaned;
    }

    internal static string MakeUniqueSheetName(string baseName, HashSet<string> used)
    {
        if (!used.Contains(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var suffix = $" ({i})";
            var trimmed = baseName.Length + suffix.Length > 31
                ? baseName[..(31 - suffix.Length)]
                : baseName;
            var candidate = trimmed + suffix;
            if (!used.Contains(candidate)) return candidate;
        }

        return Guid.NewGuid().ToString("N")[..31];
    }

    private sealed record ContactRow(
        string? DisplayName,
        string? FirstName,
        string? LastName,
        string? Email,
        string? JobTitle,
        string? Department,
        string? OfficeLocation,
        string? BusinessPhone,
        string? MobilePhone,
        string? CompanyName,
        int SourceUserId);
}
```

- [ ] **Step 2: Run the unit tests**

Run:
```
dotnet test tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj --filter FullyQualifiedName~ContactExportServiceTests
```

Expected: all tests pass.

- [ ] **Step 3: Stop for user commit**

Tell the user: "Task 4 complete: `ContactExportService` implemented, all unit tests pass."

---

## Task 5: Register the service in DI

**Files:**
- Modify: `api/Program.cs` (around line 110, near existing `AddScoped<IDDGResolver, ...>` registration)

- [ ] **Step 1: Locate the existing service registrations**

In `api/Program.cs`, find the block that includes:

```csharp
builder.Services.AddScoped<IDDGResolver, DDGResolver>();
builder.Services.AddSingleton<IFilterConverter, FilterConverter>();
```

- [ ] **Step 2: Add the new registration immediately below**

Insert this line after the `AddSingleton<IFilterConverter, ...>` line:

```csharp
builder.Services.AddScoped<IContactExportService, ContactExportService>();
```

(Add `using AFHSync.Api.Services;` at the top of the file if it isn't already imported — it is already used elsewhere, so it should be present.)

- [ ] **Step 3: Build to verify**

Run:
```
dotnet build api/AFHSync.Api.csproj
```

Expected: build succeeds.

- [ ] **Step 4: Stop for user commit**

Tell the user: "Task 5 complete: service registered in DI."

---

## Task 6: Write the failing controller integration test

**Files:**
- Create: `tests/AFHSync.Tests.Integration/Api/ExportsControllerTests.cs`

- [ ] **Step 1: Add the test file**

Create `tests/AFHSync.Tests.Integration/Api/ExportsControllerTests.cs` with:

```csharp
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
```

- [ ] **Step 2: Run the integration test to confirm it fails**

Run:
```
dotnet test tests/AFHSync.Tests.Integration/AFHSync.Tests.Integration.csproj --filter FullyQualifiedName~ExportsControllerTests
```

Expected: tests fail. The auth test gets a 404 (not 401) because the endpoint doesn't exist yet, and the second test fails on the status assertion.

- [ ] **Step 3: Stop for user commit**

Tell the user: "Task 6 complete: failing integration test written for the export endpoint."

---

## Task 7: Implement `ExportsController`

**Files:**
- Create: `api/Controllers/ExportsController.cs`

- [ ] **Step 1: Create the controller**

Create `api/Controllers/ExportsController.cs` with:

```csharp
using AFHSync.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/exports")]
public class ExportsController : ControllerBase
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IContactExportService _exportService;
    private readonly ILogger<ExportsController> _logger;

    public ExportsController(IContactExportService exportService, ILogger<ExportsController> logger)
    {
        _exportService = exportService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/exports/contacts.xlsx — Download an Excel workbook with one
    /// sheet per tunnel listing live (non-stale) contacts being synced.
    /// </summary>
    [HttpGet("contacts.xlsx")]
    public async Task<IActionResult> GetContactsXlsx(CancellationToken ct)
    {
        try
        {
            var bytes = await _exportService.BuildContactsWorkbookAsync(ct);
            var filename = $"afh-sync-contacts-{DateTime.UtcNow:yyyy-MM-dd}.xlsx";

            // Set Content-Disposition explicitly so the frontend can read the
            // intended filename off the response.
            var contentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = filename
            };
            Response.Headers.ContentDisposition = contentDisposition.ToString();

            return File(bytes, XlsxContentType);
        }
        catch (OperationCanceledException)
        {
            // Client cancelled — let ASP.NET handle the 499-equivalent
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build contacts export workbook");
            return StatusCode(500, new { message = "Failed to build contacts export." });
        }
    }
}
```

- [ ] **Step 2: Run the integration tests**

Run:
```
dotnet test tests/AFHSync.Tests.Integration/AFHSync.Tests.Integration.csproj --filter FullyQualifiedName~ExportsControllerTests
```

Expected: both tests pass.

- [ ] **Step 3: Run the full test suite to verify nothing else broke**

Run:
```
dotnet test
```

Expected: all tests pass.

- [ ] **Step 4: Stop for user commit**

Tell the user: "Task 7 complete: `ExportsController` implemented, all tests pass."

---

## Task 8: Add the "Export" sidebar entry

**Files:**
- Modify: `frontend/src/components/layout/Sidebar.tsx`

- [ ] **Step 1: Add the `Download` icon import**

In `frontend/src/components/layout/Sidebar.tsx`, modify the `lucide-react` import block to include `Download`. The full import becomes:

```ts
import {
  LayoutDashboard,
  Cable,
  Phone,
  SlidersHorizontal,
  ClipboardList,
  Settings,
  LogOut,
  Trash2,
  UserSearch,
  Download,
} from 'lucide-react';
```

- [ ] **Step 2: Add the nav entry**

Update the `navItems` array to include the Export entry between User Lookup and Cleanup:

```ts
const navItems = [
  { label: 'Dashboard', href: '/', icon: LayoutDashboard },
  { label: 'Tunnels', href: '/tunnels', icon: Cable },
  { label: 'Targets', href: '/lists', icon: Phone },
  { label: 'Field Profiles', href: '/fields', icon: SlidersHorizontal },
  { label: 'Runs & Logs', href: '/runs', icon: ClipboardList },
  { label: 'User Lookup', href: '/users', icon: UserSearch },
  { label: 'Export', href: '/exports', icon: Download },
  { label: 'Cleanup', href: '/cleanup', icon: Trash2 },
  { label: 'Settings', href: '/settings', icon: Settings },
];
```

- [ ] **Step 3: Verify the typecheck and lint pass**

Run from the `frontend/` directory:
```
npx tsc --noEmit
```

Expected: no errors.

- [ ] **Step 4: Stop for user commit**

Tell the user: "Task 8 complete: Export entry added to the sidebar."

---

## Task 9: Build the `/exports` page

**Files:**
- Create: `frontend/src/app/(app)/exports/page.tsx`

- [ ] **Step 1: Create the page**

Create `frontend/src/app/(app)/exports/page.tsx` with:

```tsx
'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { Download, Loader2 } from 'lucide-react';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

const FALLBACK_FILENAME = 'afh-sync-contacts.xlsx';

function filenameFromContentDisposition(header: string | null): string {
  if (!header) return FALLBACK_FILENAME;
  // Matches: filename="afh-sync-contacts-2026-05-08.xlsx" or filename=...
  const match = /filename\*?=(?:UTF-8'')?["']?([^"';]+)/i.exec(header);
  return match?.[1] ?? FALLBACK_FILENAME;
}

export default function ExportsPage() {
  const [downloading, setDownloading] = useState(false);

  async function handleDownload() {
    setDownloading(true);
    try {
      const res = await fetch('/api/exports/contacts.xlsx', {
        credentials: 'include',
      });

      if (res.status === 401) {
        window.location.href = '/login';
        return;
      }

      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.message ?? `Export failed: ${res.status}`);
      }

      const blob = await res.blob();
      const filename = filenameFromContentDisposition(
        res.headers.get('content-disposition'),
      );
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);

      toast.success('Download started');
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Export failed';
      toast.error(message);
    } finally {
      setDownloading(false);
    }
  }

  return (
    <div className="space-y-6">
      <PageHeader
        title="Export"
        subtitle="Download data from the sync platform."
      />

      <Card>
        <CardHeader>
          <CardTitle>Synced contacts by tunnel</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-sm text-muted-foreground">
            Downloads an Excel workbook with one sheet per tunnel. Each sheet
            lists the contacts currently being synced through that tunnel
            (live contacts only — stale contacts are excluded).
          </p>
          <Button onClick={handleDownload} disabled={downloading}>
            {downloading ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <Download className="mr-2 h-4 w-4" />
            )}
            {downloading ? 'Building workbook…' : 'Download .xlsx'}
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
```

- [ ] **Step 2: Verify imports resolve**

Confirm these components exist by checking the imports:

```
ls frontend/src/components/PageHeader.tsx
ls frontend/src/components/ui/button.tsx
ls frontend/src/components/ui/card.tsx
```

Expected: all three files exist. (They are used elsewhere in the app — see e.g. `frontend/src/app/(app)/page.tsx`.)

- [ ] **Step 3: Verify the typecheck passes**

Run from the `frontend/` directory:
```
npx tsc --noEmit
```

Expected: no errors.

- [ ] **Step 4: Stop for user commit**

Tell the user: "Task 9 complete: `/exports` page implemented."

---

## Task 10: Manual end-to-end smoke test

**Files:**
- (none modified)

- [ ] **Step 1: Start the dev stack**

Run from repo root (this is what `compose.dev.yaml` is for):
```
docker compose -f compose.dev.yaml up -d
```

Wait until the API and frontend are reachable. (Check `docker compose ps` for healthy status.)

- [ ] **Step 2: Log in and navigate to Export**

In a browser:
1. Open the frontend (whatever port `compose.dev.yaml` exposes; typically `http://localhost:3000`).
2. Log in with admin credentials.
3. Confirm a new "Export" item appears in the left sidebar between "User Lookup" and "Cleanup".
4. Click it — the page loads with a "Download .xlsx" button.

- [ ] **Step 3: Trigger the download**

Click "Download .xlsx".

Expected:
- Button shows a spinner and "Building workbook…" while in flight.
- Browser prompts to save a file named `afh-sync-contacts-YYYY-MM-DD.xlsx`.
- Toast appears: "Download started".

- [ ] **Step 4: Open the downloaded workbook**

Open the file in Excel (or `numbers`/LibreOffice).

Verify:
- One sheet per active tunnel; sheet names match (sanitized) tunnel names.
- Header row is bold, row 1 is frozen.
- Columns: Display Name, First Name, Last Name, Email, Job Title, Department, Office, Business Phone, Mobile Phone, Company.
- For at least one tunnel you know contains synced contacts, the contact rows look right (no stale contacts, no duplicates within a sheet).
- An empty tunnel (if any) has only the header row.

- [ ] **Step 5: Stop for user commit**

Tell the user: "Task 10 complete: smoke test passed. Feature is ready."

---

## Self-Review

**Spec coverage:**
- Backend endpoint `GET /api/exports/contacts.xlsx` — Tasks 6–7 ✓
- Service `IContactExportService` + impl with sanitization, dedup, sort, ClosedXML — Tasks 2, 4 ✓
- ClosedXML dependency added — Task 1 ✓
- DI registration — Task 5 ✓
- Sheet schema (10 columns, header order matches spec) — Task 4 (impl) + Task 3 (test asserts headers) ✓
- Empty tunnel still gets a sheet — Task 3 (`EmptyTunnelStillGetsSheet`) + Task 4 ✓
- Sheet-name sanitization rules (≤31, replace `:\/?*[]`, dedup) — Tasks 3 + 4 ✓
- `Content-Disposition: attachment; filename=…` set explicitly — Task 7 ✓
- Filename pattern `afh-sync-contacts-{yyyy-MM-dd}.xlsx` (UTC) — Task 7 ✓
- 500 with `{ message }` JSON body on failure — Task 7 ✓
- Sidebar entry "Export" with `Download` icon → `/exports` — Task 8 ✓
- `/exports` page: PageHeader + card + button + spinner + toast + Content-Disposition filename parsing — Task 9 ✓
- Browser download via blob + object URL — Task 9 ✓
- Manual smoke test in browser — Task 10 ✓
- Out-of-scope items (date range, per-tunnel export, async delivery, stale toggle, Field-Profile-aware columns) intentionally not in any task ✓

**Placeholder scan:** No TBD/TODO. All code blocks are complete and runnable. Tests have concrete assertions.

**Type/name consistency:**
- Service interface `IContactExportService` and method `BuildContactsWorkbookAsync(CancellationToken)` consistent across Tasks 2, 3, 4, 5, 7.
- Sheet headers list matches between Task 3 assertion and Task 4 implementation (`Display Name`, `First Name`, …, `Company`).
- Endpoint route `/api/exports/contacts.xlsx` consistent across Tasks 6, 7, 9.
- Filename pattern `afh-sync-contacts-…` consistent in Tasks 6 (assertion) and 7 (implementation).
- DTOs and entity property names (`SourceUser.OfficeLocation`, `SourceUser.CompanyName`, `ContactSyncState.IsStale`, `ContactSyncState.TunnelId`) verified against `shared/Entities/SourceUser.cs` and `shared/Entities/ContactSyncState.cs`.
