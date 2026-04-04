using AFHSync.Api.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly AFHSyncDbContext _db;

    public DashboardController(AFHSyncDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/dashboard — System overview KPIs, last sync, warnings, and recent runs.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var activeTunnels = await _db.Tunnels.CountAsync(t => t.Status == TunnelStatus.Active);
        var totalPhoneLists = await _db.PhoneLists.CountAsync();
        var totalTargetUsers = await _db.TargetMailboxes.CountAsync(m => m.IsActive);

        // Last completed SyncRun
        var lastCompletedRun = await _db.SyncRuns
            .Where(r => r.Status == SyncStatus.Success || r.Status == SyncStatus.Warning)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync();

        DashboardLastSyncDto? lastSync = lastCompletedRun is not null
            ? new DashboardLastSyncDto(
                lastCompletedRun.Id,
                EnumHelpers.ToPgName(lastCompletedRun.Status),
                lastCompletedRun.StartedAt,
                lastCompletedRun.DurationMs,
                lastCompletedRun.ContactsCreated,
                lastCompletedRun.ContactsUpdated,
                lastCompletedRun.ContactsRemoved,
                lastCompletedRun.PhotosUpdated)
            : null;

        // Recent runs (last 5)
        var recentRuns = await _db.SyncRuns
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .Take(5)
            .AsNoTracking()
            .ToListAsync();

        var recentRunDtos = recentRuns.Select(r => new DashboardRecentRunDto(
            r.Id,
            EnumHelpers.ToPgName(r.RunType),
            EnumHelpers.ToPgName(r.Status),
            r.StartedAt,
            r.DurationMs,
            r.ContactsUpdated
        )).ToArray();

        // Warnings: tunnels that have had failures in their last sync run items
        // For v1 simplified version: check for tunnels with failed SyncRunItems in the latest run
        var warnings = new List<DashboardWarningDto>();
        if (lastCompletedRun is not null && lastCompletedRun.TunnelsFailed > 0)
        {
            var failedTunnelIds = await _db.SyncRunItems
                .Where(i => i.SyncRunId == lastCompletedRun.Id && i.Action == "failed")
                .Select(i => i.TunnelId)
                .Distinct()
                .Take(10)
                .ToListAsync();

            foreach (var tunnelId in failedTunnelIds)
            {
                warnings.Add(new DashboardWarningDto(
                    "tunnel_failure",
                    tunnelId,
                    $"Tunnel {tunnelId} had failures in the last sync run."
                ));
            }
        }

        var dto = new DashboardDto(
            activeTunnels,
            totalPhoneLists,
            totalTargetUsers,
            lastSync,
            warnings.ToArray(),
            recentRunDtos
        );

        return Ok(dto);
    }
}
