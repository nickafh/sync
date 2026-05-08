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
