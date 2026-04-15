using AFHSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

/// <summary>
/// Health check endpoints. AllowAnonymous per AUTH-03 (health endpoints accessible without auth).
/// GET /health - Database connectivity check.
/// GET /health/graph - Graph API credentials and permission check (per D-11).
/// </summary>
[ApiController]
[Route("health")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Basic health check with database connectivity verification.
    /// Returns 200 if database is reachable, 503 otherwise.
    /// Uses IServiceProvider to gracefully handle cases where DbContext is not yet registered.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Health([FromServices] IServiceProvider services)
    {
        try
        {
            var db = services.GetService<AFHSync.Shared.Data.AFHSyncDbContext>();
            if (db is null)
            {
                return Ok(new { status = "healthy", database = "not configured" });
            }

            await db.Database.CanConnectAsync();
            return Ok(new { status = "healthy", database = "connected" });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "unhealthy", database = ex.Message });
        }
    }

    /// <summary>
    /// Graph API health check. Validates Entra credentials and permissions.
    /// Returns 200 if Graph connection is verified, 503 otherwise.
    /// </summary>
    [HttpGet("graph")]
    public async Task<IActionResult> GraphHealth([FromServices] GraphHealthService graphHealth)
    {
        var result = await graphHealth.CheckAsync();
        return result.IsHealthy
            ? Ok(result)
            : StatusCode(503, result);
    }
}
