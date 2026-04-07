using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

/// <summary>
/// Sync run trigger and polling endpoints.
/// POST /api/sync-runs — Enqueue a sync run via Hangfire (SCHD-02, SCHD-03, SCHD-04).
/// GET /api/sync-runs — Paginated run history.
/// GET /api/sync-runs/{id} — Run detail.
/// GET /api/sync-runs/{id}/items — Per-item log with action filter.
/// </summary>
[ApiController]
[Route("api/sync-runs")]
public class SyncRunsController : ControllerBase
{
    /// <summary>
    /// POST /api/sync-runs — Trigger a new sync run.
    /// Creates a pending SyncRun, enqueues Hangfire job, returns runId immediately (D-09, D-11).
    /// Returns 409 Conflict if a run is already in progress (SCHD-05).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> TriggerSync(
        [FromBody] TriggerSyncRequest request,
        [FromServices] AFHSyncDbContext db,
        [FromServices] IBackgroundJobClient jobs)
    {
        // Concurrent run prevention (SCHD-05, D-10)
        var isActive = await db.SyncRuns.AnyAsync(r => r.Status == SyncStatus.Running || r.Status == SyncStatus.Pending);
        if (isActive)
            return Conflict(new { message = "A sync run is already in progress" });

        // Determine RunType from request
        var runType = request.IsDryRun ? RunType.DryRun : RunType.Manual;

        // Create a pending SyncRun record
        var run = new AFHSync.Shared.Entities.SyncRun
        {
            RunType = runType,
            Status = SyncStatus.Pending,
            IsDryRun = request.IsDryRun,
            CreatedAt = DateTime.UtcNow
        };

        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();

        // Enqueue Hangfire fire-and-forget job (D-09)
        // ISyncEngine.RunAsync takes int? tunnelId — pass first tunnel ID if specified, null for all
        int? tunnelId = request.TunnelIds?.FirstOrDefault();
        jobs.Enqueue<ISyncEngine>(engine =>
            engine.RunAsync(tunnelId, runType, request.IsDryRun, CancellationToken.None));

        return Ok(new { runId = run.Id });
    }

    /// <summary>
    /// GET /api/sync-runs?page=1&amp;pageSize=20 — Paginated run history.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetRuns(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromServices] AFHSyncDbContext db = null!)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var runs = await db.SyncRuns
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new SyncRunDto(
                r.Id,
                EnumHelpers.ToPgName(r.RunType),
                EnumHelpers.ToPgName(r.Status),
                r.IsDryRun,
                r.StartedAt,
                r.CompletedAt,
                r.DurationMs,
                r.ContactsCreated,
                r.ContactsUpdated,
                r.ContactsRemoved,
                r.ContactsSkipped,
                r.ContactsFailed,
                r.PhotosUpdated,
                r.PhotosFailed,
                r.ThrottleEvents))
            .ToListAsync();

        return Ok(runs);
    }

    /// <summary>
    /// GET /api/sync-runs/{id} — Run detail with per-tunnel summaries (RLOG-02, RLOG-03).
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetRun(
        int id,
        [FromServices] AFHSyncDbContext db)
    {
        var run = await db.SyncRuns.FindAsync(id);
        if (run is null)
            return NotFound(new { message = $"Sync run {id} not found" });

        // Compute per-tunnel summaries from SyncRunItems grouped by TunnelId
        var tunnelSummaries = await db.SyncRunItems
            .Where(i => i.SyncRunId == id)
            .GroupBy(i => i.TunnelId)
            .Select(g => new
            {
                TunnelId = g.Key,
                Created = g.Count(i => i.Action == "created"),
                Updated = g.Count(i => i.Action == "updated"),
                Removed = g.Count(i => i.Action == "removed"),
                Skipped = g.Count(i => i.Action == "skipped"),
                Failed = g.Count(i => i.Action == "failed"),
                Photos = g.Count(i => i.Action == "photo_updated"),
                PhotosFailed = g.Count(i => i.Action == "photo_failed"),
                Errors = g.Where(i => i.ErrorMessage != null)
                          .Select(i => i.ErrorMessage!)
                          .ToArray()
            })
            .ToListAsync();

        // Resolve tunnel names
        var tunnelIds = tunnelSummaries
            .Where(s => s.TunnelId.HasValue)
            .Select(s => s.TunnelId!.Value)
            .Distinct()
            .ToList();

        var tunnelNames = await db.Tunnels
            .Where(t => tunnelIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        var summaryDtos = tunnelSummaries.Select(s => new TunnelRunSummaryDto(
            s.TunnelId,
            s.TunnelId.HasValue && tunnelNames.TryGetValue(s.TunnelId.Value, out var name)
                ? name : "Unknown",
            s.Created,
            s.Updated,
            s.Removed,
            s.Skipped,
            s.Failed,
            s.Photos,
            s.PhotosFailed,
            s.Errors
        )).ToArray();

        return Ok(new SyncRunDetailDto(
            run.Id,
            EnumHelpers.ToPgName(run.RunType),
            EnumHelpers.ToPgName(run.Status),
            run.IsDryRun,
            run.StartedAt,
            run.CompletedAt,
            run.DurationMs,
            run.TunnelsProcessed,
            run.TunnelsWarned,
            run.ContactsCreated,
            run.ContactsUpdated,
            run.ContactsRemoved,
            run.ContactsSkipped,
            run.ContactsFailed,
            run.PhotosUpdated,
            run.PhotosFailed,
            run.ThrottleEvents,
            run.ErrorSummary,
            summaryDtos));
    }

    /// <summary>
    /// GET /api/sync-runs/{id}/items?page=1&amp;pageSize=50&amp;action= — Per-item log.
    /// </summary>
    [HttpGet("{id:int}/items")]
    public async Task<IActionResult> GetRunItems(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? action = null,
        [FromServices] AFHSyncDbContext db = null!)
    {
        // Verify the run exists
        var runExists = await db.SyncRuns.AnyAsync(r => r.Id == id);
        if (!runExists)
            return NotFound(new { message = $"Sync run {id} not found" });

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        var query = db.SyncRunItems
            .Where(i => i.SyncRunId == id);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(i => i.Action == action);

        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new SyncRunItemDto(
                i.Id,
                i.TunnelId,
                i.SourceUserId,
                i.SourceUser != null ? i.SourceUser.DisplayName : null,
                i.Action,
                i.FieldChanges,
                i.ErrorMessage,
                i.CreatedAt))
            .ToListAsync();

        return Ok(items);
    }
}
