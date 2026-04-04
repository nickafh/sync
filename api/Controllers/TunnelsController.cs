using AFHSync.Api.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/tunnels")]
public class TunnelsController : ControllerBase
{
    private readonly AFHSyncDbContext _db;

    public TunnelsController(AFHSyncDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/tunnels — List all tunnels with summary info including source, targets, and last sync.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tunnels = await _db.Tunnels
            .Include(t => t.TunnelPhoneLists)
                .ThenInclude(tp => tp.PhoneList)
            .Include(t => t.FieldProfile)
            .AsNoTracking()
            .ToListAsync();

        var activeTargetUsers = await _db.TargetMailboxes.CountAsync(m => m.IsActive);

        var lastRuns = await _db.SyncRuns
            .OrderByDescending(r => r.CompletedAt)
            .Take(50)
            .AsNoTracking()
            .ToListAsync();

        var result = new List<TunnelDto>();
        foreach (var t in tunnels)
        {
            var estimatedContacts = await _db.ContactSyncStates
                .Where(c => c.TunnelId == t.Id)
                .Select(c => c.SourceUserId)
                .Distinct()
                .CountAsync();

            // For AllUsers scope, use the global active target user count
            var estimatedTargetUsers = t.TargetScope == TargetScope.AllUsers
                ? activeTargetUsers
                : 0;

            // Last sync: most recent completed SyncRun (tunnel-level aggregates are stored at SyncRun level for now)
            var lastRun = lastRuns.FirstOrDefault(r => r.Status == SyncStatus.Success || r.Status == SyncStatus.Warning);
            TunnelLastSyncDto? lastSync = lastRun is not null
                ? new TunnelLastSyncDto(
                    EnumHelpers.ToPgName(lastRun.Status),
                    lastRun.CompletedAt,
                    lastRun.ContactsUpdated)
                : null;

            result.Add(new TunnelDto(
                t.Id,
                t.Name,
                EnumHelpers.ToPgName(t.SourceType),
                t.SourceIdentifier,
                t.SourceDisplayName,
                t.SourceSmtpAddress,
                EnumHelpers.ToPgName(t.TargetScope),
                EnumHelpers.ToPgName(t.Status),
                EnumHelpers.ToPgName(t.StalePolicy),
                t.StaleHoldDays,
                t.FieldProfileId,
                t.FieldProfile?.Name,
                t.TunnelPhoneLists.Select(tp => new TunnelTargetListDto(tp.PhoneList.Id, tp.PhoneList.Name)).ToArray(),
                estimatedContacts,
                estimatedTargetUsers,
                lastSync
            ));
        }

        return Ok(result);
    }

    /// <summary>
    /// GET /api/tunnels/{id} — Full tunnel detail.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var tunnel = await _db.Tunnels
            .Include(t => t.TunnelPhoneLists)
                .ThenInclude(tp => tp.PhoneList)
            .Include(t => t.FieldProfile)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {id} not found." });

        var dto = new TunnelDetailDto(
            tunnel.Id,
            tunnel.Name,
            EnumHelpers.ToPgName(tunnel.SourceType),
            tunnel.SourceIdentifier,
            tunnel.SourceDisplayName,
            tunnel.SourceSmtpAddress,
            EnumHelpers.ToPgName(tunnel.TargetScope),
            tunnel.TargetUserFilter,
            EnumHelpers.ToPgName(tunnel.Status),
            EnumHelpers.ToPgName(tunnel.StalePolicy),
            tunnel.StaleHoldDays,
            tunnel.FieldProfileId,
            tunnel.FieldProfile?.Name,
            tunnel.TunnelPhoneLists.Select(tp => new TunnelTargetListDto(tp.PhoneList.Id, tp.PhoneList.Name)).ToArray(),
            tunnel.CreatedAt,
            tunnel.UpdatedAt
        );

        return Ok(dto);
    }

    /// <summary>
    /// POST /api/tunnels — Create a new tunnel storing DDG reference and Graph filter (DDG-04).
    /// SourceIdentifier stores the Graph $filter string.
    /// SourceDisplayName stores the human-readable DDG display name.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTunnelRequest request)
    {
        if (!Enum.TryParse<SourceType>(request.SourceType, ignoreCase: true, out var sourceType))
            return BadRequest(new { message = $"Invalid SourceType: {request.SourceType}" });

        if (!Enum.TryParse<TargetScope>(request.TargetScope, ignoreCase: true, out var targetScope))
            return BadRequest(new { message = $"Invalid TargetScope: {request.TargetScope}" });

        if (!Enum.TryParse<StalePolicy>(request.StalePolicy, ignoreCase: true, out var stalePolicy))
            return BadRequest(new { message = $"Invalid StalePolicy: {request.StalePolicy}" });

        var tunnel = new Tunnel
        {
            Name = request.Name,
            SourceType = sourceType,
            SourceIdentifier = request.SourceIdentifier,    // Graph $filter per DDG-04
            SourceDisplayName = request.SourceDisplayName,  // DDG display name per DDG-04
            SourceSmtpAddress = request.SourceSmtpAddress,  // DDG SMTP address per DDG-04
            TargetScope = targetScope,
            FieldProfileId = request.FieldProfileId,
            StalePolicy = stalePolicy,
            StaleHoldDays = request.StaleDays,
            Status = TunnelStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Tunnels.Add(tunnel);
        await _db.SaveChangesAsync();

        // Create TunnelPhoneList join records for each target list
        foreach (var listId in request.TargetListIds)
        {
            _db.TunnelPhoneLists.Add(new TunnelPhoneList
            {
                TunnelId = tunnel.Id,
                PhoneListId = listId,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = tunnel.Id }, new { id = tunnel.Id });
    }

    /// <summary>
    /// PUT /api/tunnels/{id} — Update tunnel properties including name, source, targets, field profile, and stale policy.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateTunnelRequest request)
    {
        var tunnel = await _db.Tunnels.FindAsync(id);
        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {id} not found." });

        if (!Enum.TryParse<SourceType>(request.SourceType, ignoreCase: true, out var sourceType))
            return BadRequest(new { message = $"Invalid SourceType: {request.SourceType}" });

        if (!Enum.TryParse<TargetScope>(request.TargetScope, ignoreCase: true, out var targetScope))
            return BadRequest(new { message = $"Invalid TargetScope: {request.TargetScope}" });

        if (!Enum.TryParse<StalePolicy>(request.StalePolicy, ignoreCase: true, out var stalePolicy))
            return BadRequest(new { message = $"Invalid StalePolicy: {request.StalePolicy}" });

        tunnel.Name = request.Name;
        tunnel.SourceType = sourceType;
        tunnel.SourceIdentifier = request.SourceIdentifier;
        tunnel.SourceDisplayName = request.SourceDisplayName;
        tunnel.SourceSmtpAddress = request.SourceSmtpAddress;
        tunnel.TargetScope = targetScope;
        tunnel.FieldProfileId = request.FieldProfileId;
        tunnel.StalePolicy = stalePolicy;
        tunnel.StaleHoldDays = request.StaleDays;
        tunnel.UpdatedAt = DateTime.UtcNow;

        // Replace TunnelPhoneList join records
        var existingLinks = await _db.TunnelPhoneLists
            .Where(tp => tp.TunnelId == id)
            .ToListAsync();
        _db.TunnelPhoneLists.RemoveRange(existingLinks);

        foreach (var listId in request.TargetListIds)
        {
            _db.TunnelPhoneLists.Add(new TunnelPhoneList
            {
                TunnelId = tunnel.Id,
                PhoneListId = listId,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Tunnel updated." });
    }

    /// <summary>
    /// PUT /api/tunnels/{id}/status — Activate or deactivate a tunnel.
    /// </summary>
    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdateRequest request)
    {
        var tunnel = await _db.Tunnels.FindAsync(id);
        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {id} not found." });

        if (!Enum.TryParse<TunnelStatus>(request.Status, ignoreCase: true, out var status))
            return BadRequest(new { message = $"Invalid status: {request.Status}. Use 'active' or 'inactive'." });

        tunnel.Status = status;
        tunnel.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = $"Tunnel status updated to {request.Status}." });
    }

    /// <summary>
    /// DELETE /api/tunnels/{id} — Delete tunnel (blocked if active sync state exists).
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var tunnel = await _db.Tunnels.FindAsync(id);
        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {id} not found." });

        var hasActiveSyncState = await _db.ContactSyncStates.AnyAsync(c => c.TunnelId == id);
        if (hasActiveSyncState)
            return Conflict(new { message = "Tunnel has active sync state. Deactivate first." });

        _db.Tunnels.Remove(tunnel);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record StatusUpdateRequest(string Status);
