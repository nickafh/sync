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
            .Include(s => s.SourceUser)
            .AsNoTracking()
            .ToListAsync(ct);

        var projectedRows = rows.Select(s => new
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
        }).ToList();

        var byTunnel = projectedRows
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
