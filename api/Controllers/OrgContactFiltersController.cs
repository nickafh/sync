using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/tunnels/{tunnelId:int}/org-contact-filters")]
public class OrgContactFiltersController : ControllerBase
{
    private readonly AFHSyncDbContext _db;

    public OrgContactFiltersController(AFHSyncDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/tunnels/{tunnelId}/org-contact-filters — Get all org contact filters for a tunnel.
    /// Returns which org contacts are excluded for this tunnel.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetFilters(int tunnelId)
    {
        var exists = await _db.Tunnels.AnyAsync(t => t.Id == tunnelId);
        if (!exists)
            return NotFound(new { message = $"Tunnel {tunnelId} not found." });

        var filters = await _db.OrgContactFilters
            .Where(f => f.TunnelId == tunnelId)
            .OrderBy(f => f.DisplayName)
            .Select(f => new OrgContactFilterInput(
                f.OrgContactId,
                f.DisplayName,
                f.Email,
                f.CompanyName,
                f.IsExcluded
            ))
            .AsNoTracking()
            .ToListAsync();

        return Ok(filters);
    }

    /// <summary>
    /// PUT /api/tunnels/{tunnelId}/org-contact-filters — Bulk upsert org contact filters.
    /// Receives the full list of org contacts with their excluded state.
    /// Uses INSERT ... ON CONFLICT to upsert efficiently.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateFilters(int tunnelId, [FromBody] UpdateOrgContactFiltersRequest request)
    {
        var exists = await _db.Tunnels.AnyAsync(t => t.Id == tunnelId);
        if (!exists)
            return NotFound(new { message = $"Tunnel {tunnelId} not found." });

        if (request.Filters is null)
            return BadRequest(new { message = "Filters array is required." });

        // Allow empty array — clears all filters for this tunnel
        if (request.Filters.Length == 0)
        {
            var cleared = await _db.OrgContactFilters
                .Where(f => f.TunnelId == tunnelId)
                .ExecuteDeleteAsync();
            return Ok(new { message = $"Cleared {cleared} filters." });
        }

        var now = DateTime.UtcNow;

        // Remove existing filters for this tunnel and replace with new set
        var existing = await _db.OrgContactFilters
            .Where(f => f.TunnelId == tunnelId)
            .ToListAsync();
        _db.OrgContactFilters.RemoveRange(existing);

        foreach (var filter in request.Filters)
        {
            _db.OrgContactFilters.Add(new OrgContactFilter
            {
                TunnelId = tunnelId,
                OrgContactId = filter.OrgContactId,
                DisplayName = filter.DisplayName,
                Email = filter.Email,
                CompanyName = filter.CompanyName,
                IsExcluded = filter.IsExcluded,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _db.SaveChangesAsync();

        var excludedCount = request.Filters.Count(f => f.IsExcluded);
        return Ok(new { message = $"Saved {request.Filters.Length} filters ({excludedCount} excluded)." });
    }

    /// <summary>
    /// PATCH /api/tunnels/{tunnelId}/org-contact-filters/bulk-exclude — Bulk toggle exclusion by company name.
    /// </summary>
    [HttpPatch("bulk-exclude")]
    public async Task<IActionResult> BulkExclude(int tunnelId, [FromBody] BulkExcludeRequest request)
    {
        var count = await _db.OrgContactFilters
            .Where(f => f.TunnelId == tunnelId && f.CompanyName == request.CompanyName)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.IsExcluded, request.IsExcluded)
                .SetProperty(f => f.UpdatedAt, DateTime.UtcNow));

        return Ok(new { count, message = $"Updated {count} contacts for company '{request.CompanyName}'." });
    }
}

public record BulkExcludeRequest(string CompanyName, bool IsExcluded);
