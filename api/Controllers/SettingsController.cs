using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

/// <summary>
/// Settings CRUD with Hangfire cron reschedule.
/// GET /api/settings — All app settings.
/// PUT /api/settings — Update settings; reschedules Hangfire recurring job if sync_schedule_cron changes (D-18).
/// </summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    /// <summary>
    /// GET /api/settings — Returns all application settings.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetSettings([FromServices] AFHSyncDbContext db)
    {
        var settings = await db.AppSettings
            .OrderBy(s => s.Key)
            .Select(s => new SettingsDto(s.Key, s.Value, s.Description))
            .ToListAsync();
        return Ok(settings);
    }

    /// <summary>
    /// PUT /api/settings — Update one or more settings.
    /// If sync_schedule_cron is changed, reschedules the Hangfire recurring sync job (D-18).
    /// Returns 404 if any setting key is not found.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] SettingsUpdateRequest request,
        [FromServices] AFHSyncDbContext db,
        [FromServices] IRecurringJobManager recurringJobs)
    {
        foreach (var setting in request.Settings)
        {
            var existing = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == setting.Key);

            if (existing == null)
                return NotFound(new { message = $"Setting '{setting.Key}' not found" });

            existing.Value = setting.Value;
            existing.UpdatedAt = DateTime.UtcNow;

            // D-18: If sync schedule changed, reschedule Hangfire recurring job
            if (setting.Key == "sync_schedule_cron")
            {
                recurringJobs.AddOrUpdate<ISyncEngine>(
                    "sync-all",
                    engine => engine.RunAsync(null, RunType.Scheduled, false, CancellationToken.None),
                    setting.Value);
            }
        }

        await db.SaveChangesAsync();
        return Ok();
    }
}
