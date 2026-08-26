using AFHSync.Api.DTOs;
using AFHSync.Shared.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

/// <summary>
/// Target-mailbox health. The picker list lives at /api/tunnels/target-mailboxes; this
/// controller reports on the mailboxes the worker cannot deliver to.
/// </summary>
[ApiController]
[Route("api/targets")]
public class TargetsController(AFHSyncDbContext db) : ControllerBase
{
    /// <summary>
    /// GET /api/targets/unavailable — active target mailboxes stamped unavailable (§2.1),
    /// oldest first, with totals for an "N of M" header. The worker re-probes each one weekly
    /// and clears the stamp on the first successful folder lookup.
    /// </summary>
    [HttpGet("unavailable")]
    public async Task<ActionResult<UnavailableMailboxesDto>> GetUnavailable(CancellationToken ct)
    {
        var totalActive = await db.TargetMailboxes.CountAsync(m => m.IsActive, ct);

        var items = await db.TargetMailboxes
            .Where(m => m.IsActive && m.MailboxUnavailableAt != null)
            .OrderBy(m => m.MailboxUnavailableAt)
            .ThenBy(m => m.Email)
            .Select(m => new UnavailableMailboxDto(
                m.Id,
                m.DisplayName,
                m.Email,
                m.MailboxUnavailableAt!.Value,
                m.MailboxLastProbedAt,
                m.MailboxUnavailableReason))
            .ToListAsync(ct);

        return Ok(new UnavailableMailboxesDto(totalActive, items.Count, items));
    }
}
