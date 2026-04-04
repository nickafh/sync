using AFHSync.Api.Data;
using AFHSync.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/phone-lists")]
public class PhoneListsController : ControllerBase
{
    private readonly AFHSyncDbContext _db;

    public PhoneListsController(AFHSyncDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/phone-lists — List all phone lists with contact counts and source tunnels.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var phoneLists = await _db.PhoneLists
            .Include(pl => pl.TunnelPhoneLists)
                .ThenInclude(tp => tp.Tunnel)
            .AsNoTracking()
            .ToListAsync();

        var result = phoneLists.Select(pl => new PhoneListDto(
            pl.Id,
            pl.Name,
            pl.ContactCount,
            pl.UserCount,
            pl.TunnelPhoneLists.Select(tp => new PhoneListSourceTunnelDto(tp.Tunnel.Id, tp.Tunnel.Name)).ToArray(),
            null // LastSyncStatus: derived from SyncRun data — not yet wired for v1
        )).ToArray();

        return Ok(result);
    }

    /// <summary>
    /// GET /api/phone-lists/{id} — Phone list detail.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var phoneList = await _db.PhoneLists
            .Include(pl => pl.TunnelPhoneLists)
                .ThenInclude(tp => tp.Tunnel)
            .AsNoTracking()
            .FirstOrDefaultAsync(pl => pl.Id == id);

        if (phoneList is null)
            return NotFound(new { message = $"Phone list {id} not found." });

        var dto = new PhoneListDetailDto(
            phoneList.Id,
            phoneList.Name,
            phoneList.Description,
            phoneList.ExchangeFolderId,
            phoneList.ContactCount,
            phoneList.UserCount,
            phoneList.TunnelPhoneLists.Select(tp => new PhoneListSourceTunnelDto(tp.Tunnel.Id, tp.Tunnel.Name)).ToArray(),
            phoneList.CreatedAt,
            phoneList.UpdatedAt
        );

        return Ok(dto);
    }

    /// <summary>
    /// GET /api/phone-lists/{id}/contacts?page=1&amp;pageSize=20 — Paginated contacts in a phone list.
    /// Returns distinct source users from ContactSyncState for the given phone list.
    /// </summary>
    [HttpGet("{id:int}/contacts")]
    public async Task<IActionResult> GetContacts(int id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var phoneListExists = await _db.PhoneLists.AnyAsync(pl => pl.Id == id);
        if (!phoneListExists)
            return NotFound(new { message = $"Phone list {id} not found." });

        // Select distinct source user IDs from ContactSyncState for this phone list, then join SourceUsers
        var distinctUserIds = await _db.ContactSyncStates
            .Where(c => c.PhoneListId == id)
            .Select(c => c.SourceUserId)
            .Distinct()
            .OrderBy(id => id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var contacts = await _db.SourceUsers
            .Where(u => distinctUserIds.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new ContactDto(
                u.Id,
                u.DisplayName,
                u.Email,
                u.JobTitle,
                u.Department,
                u.OfficeLocation,
                u.BusinessPhone ?? u.MobilePhone
            ))
            .AsNoTracking()
            .ToListAsync();

        return Ok(contacts);
    }
}
