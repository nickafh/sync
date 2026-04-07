using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Enums;
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

        // Compute live contact counts from ContactSyncState instead of static fields
        var phoneListIds = phoneLists.Select(pl => pl.Id).ToList();
        var contactCounts = await _db.ContactSyncStates
            .Where(c => phoneListIds.Contains(c.PhoneListId))
            .GroupBy(c => c.PhoneListId)
            .Select(g => new { PhoneListId = g.Key, ContactCount = g.Select(c => c.SourceUserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.PhoneListId, x => x.ContactCount);

        var userCounts = await _db.ContactSyncStates
            .Where(c => phoneListIds.Contains(c.PhoneListId))
            .GroupBy(c => c.PhoneListId)
            .Select(g => new { PhoneListId = g.Key, UserCount = g.Select(c => c.TargetMailboxId).Distinct().Count() })
            .ToDictionaryAsync(x => x.PhoneListId, x => x.UserCount);

        var result = phoneLists.Select(pl => new PhoneListDto(
            pl.Id,
            pl.Name,
            contactCounts.GetValueOrDefault(pl.Id, 0),
            userCounts.GetValueOrDefault(pl.Id, 0),
            EnumHelpers.ToPgName(pl.TargetScope),
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

        // Compute live counts from ContactSyncState
        var liveContactCount = await _db.ContactSyncStates
            .Where(c => c.PhoneListId == id)
            .Select(c => c.SourceUserId)
            .Distinct()
            .CountAsync();

        var liveUserCount = await _db.ContactSyncStates
            .Where(c => c.PhoneListId == id)
            .Select(c => c.TargetMailboxId)
            .Distinct()
            .CountAsync();

        var dto = new PhoneListDetailDto(
            phoneList.Id,
            phoneList.Name,
            phoneList.Description,
            phoneList.ExchangeFolderId,
            liveContactCount,
            liveUserCount,
            EnumHelpers.ToPgName(phoneList.TargetScope),
            phoneList.TargetUserFilter,
            phoneList.TunnelPhoneLists.Select(tp => new PhoneListSourceTunnelDto(tp.Tunnel.Id, tp.Tunnel.Name)).ToArray(),
            phoneList.CreatedAt,
            phoneList.UpdatedAt
        );

        return Ok(dto);
    }

    /// <summary>
    /// POST /api/phone-lists — Create a new phone list.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePhoneListRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        if (!EnumHelpers.TryFromPgName<TargetScope>(request.TargetScope, out var targetScope))
            return BadRequest(new { message = $"Invalid TargetScope: {request.TargetScope}" });

        var phoneList = new AFHSync.Shared.Entities.PhoneList
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            TargetScope = targetScope,
            TargetUserFilter = request.TargetUserFilter,
        };

        _db.PhoneLists.Add(phoneList);
        await _db.SaveChangesAsync();

        return Created($"/api/phone-lists/{phoneList.Id}", new { id = phoneList.Id, name = phoneList.Name });
    }

    /// <summary>
    /// PUT /api/phone-lists/{id} — Update a phone list.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] CreatePhoneListRequest request)
    {
        var phoneList = await _db.PhoneLists.FindAsync(id);
        if (phoneList is null)
            return NotFound(new { message = $"Phone list {id} not found." });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        if (!EnumHelpers.TryFromPgName<TargetScope>(request.TargetScope, out var targetScope))
            return BadRequest(new { message = $"Invalid TargetScope: {request.TargetScope}" });

        phoneList.Name = request.Name.Trim();
        phoneList.Description = request.Description?.Trim();
        phoneList.TargetScope = targetScope;
        phoneList.TargetUserFilter = request.TargetUserFilter;
        phoneList.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Phone list updated." });
    }

    /// <summary>
    /// DELETE /api/phone-lists/{id} — Delete a phone list.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var phoneList = await _db.PhoneLists
            .Include(pl => pl.TunnelPhoneLists)
            .FirstOrDefaultAsync(pl => pl.Id == id);

        if (phoneList is null)
            return NotFound(new { message = $"Phone list {id} not found." });

        if (phoneList.TunnelPhoneLists.Count > 0)
            return BadRequest(new { message = "Cannot delete a phone list that is used by tunnels. Remove it from all tunnels first." });

        _db.PhoneLists.Remove(phoneList);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Phone list deleted." });
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
                u.BusinessPhone ?? u.MobilePhone,
                u.MobilePhone,
                u.CompanyName,
                u.StreetAddress,
                u.City,
                u.State,
                u.PostalCode,
                u.Country
            ))
            .AsNoTracking()
            .ToListAsync();

        return Ok(contacts);
    }
}
