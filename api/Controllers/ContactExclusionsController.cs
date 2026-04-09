using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/tunnels/{tunnelId:int}/contact-exclusions")]
public class ContactExclusionsController : ControllerBase
{
    private readonly AFHSyncDbContext _db;

    public ContactExclusionsController(AFHSyncDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/tunnels/{tunnelId}/contact-exclusions/source-contacts
    /// Returns all source contacts for this tunnel (from last sync) with exclusion state.
    /// </summary>
    [HttpGet("source-contacts")]
    public async Task<IActionResult> GetSourceContacts(int tunnelId)
    {
        var exists = await _db.Tunnels.AnyAsync(t => t.Id == tunnelId);
        if (!exists)
            return NotFound(new { message = $"Tunnel {tunnelId} not found." });

        // Get source user IDs that have been synced for this tunnel
        var sourceUserIds = await _db.ContactSyncStates
            .Where(s => s.TunnelId == tunnelId)
            .Select(s => s.SourceUserId)
            .Distinct()
            .ToListAsync();

        // Load those source users
        var sourceUsers = await _db.SourceUsers
            .Where(u => sourceUserIds.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .ToListAsync();

        // Load current exclusions for this tunnel
        var exclusions = await _db.TunnelContactExclusions
            .Where(e => e.TunnelId == tunnelId)
            .Select(e => e.EntraId)
            .ToHashSetAsync();

        var contacts = sourceUsers.Select(u => new SourceContactDto(
            u.Id,
            u.EntraId,
            u.DisplayName,
            u.Email,
            u.CompanyName,
            u.JobTitle,
            u.Department,
            exclusions.Contains(u.EntraId)
        )).ToList();

        return Ok(contacts);
    }

    /// <summary>
    /// GET /api/tunnels/{tunnelId}/contact-exclusions
    /// Returns just the excluded contacts for this tunnel.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetExclusions(int tunnelId)
    {
        var exclusions = await _db.TunnelContactExclusions
            .Where(e => e.TunnelId == tunnelId)
            .OrderBy(e => e.DisplayName)
            .Select(e => new ContactExclusionInput(e.EntraId, e.DisplayName, e.Email))
            .ToListAsync();

        return Ok(exclusions);
    }

    /// <summary>
    /// PUT /api/tunnels/{tunnelId}/contact-exclusions
    /// Replace all exclusions for this tunnel. Send the list of contacts TO EXCLUDE.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateExclusions(int tunnelId, [FromBody] UpdateContactExclusionsRequest request)
    {
        var exists = await _db.Tunnels.AnyAsync(t => t.Id == tunnelId);
        if (!exists)
            return NotFound(new { message = $"Tunnel {tunnelId} not found." });

        // Clear existing exclusions
        await _db.TunnelContactExclusions
            .Where(e => e.TunnelId == tunnelId)
            .ExecuteDeleteAsync();

        // Insert new exclusions
        if (request.Exclusions is { Length: > 0 })
        {
            var now = DateTime.UtcNow;
            foreach (var exclusion in request.Exclusions)
            {
                _db.TunnelContactExclusions.Add(new TunnelContactExclusion
                {
                    TunnelId = tunnelId,
                    EntraId = exclusion.EntraId,
                    DisplayName = exclusion.DisplayName,
                    Email = exclusion.Email,
                    CreatedAt = now,
                });
            }
            await _db.SaveChangesAsync();
        }

        return Ok(new { message = $"Saved {request.Exclusions?.Length ?? 0} exclusion(s)." });
    }
}
