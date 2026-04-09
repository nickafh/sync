using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Api.Services;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/tunnels")]
public class TunnelsController : ControllerBase
{
    private readonly AFHSyncDbContext _db;
    private readonly IFilterConverter _filterConverter;
    private readonly GraphServiceClient _graphClient;
    private readonly IDDGResolver _ddgResolver;

    public TunnelsController(
        AFHSyncDbContext db,
        IFilterConverter filterConverter,
        GraphServiceClient graphClient,
        IDDGResolver ddgResolver)
    {
        _db = db;
        _filterConverter = filterConverter;
        _graphClient = graphClient;
        _ddgResolver = ddgResolver;
    }

    /// <summary>
    /// GET /api/tunnels — List all tunnels with summary info including source, targets, and last sync.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tunnels = await _db.Tunnels
            .Include(t => t.TunnelSources)
            .Include(t => t.TunnelPhoneLists)
                .ThenInclude(tp => tp.PhoneList)
            .Include(t => t.FieldProfile)
            .AsNoTracking()
            .ToListAsync();

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

            var estimatedTargetUsers = await _db.TargetMailboxes.CountAsync(m => m.IsActive);

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
                t.TunnelSources.Select(s => new TunnelSourceDto(s.Id, EnumHelpers.ToPgName(s.SourceType), s.SourceIdentifier, s.SourceDisplayName, s.SourceSmtpAddress, s.SourceFilterPlain)).ToArray(),
                EnumHelpers.ToPgName(t.Status),
                EnumHelpers.ToPgName(t.StalePolicy),
                t.StaleHoldDays,
                t.FieldProfileId,
                t.FieldProfile?.Name,
                t.TunnelPhoneLists.Select(tp => new TunnelTargetListDto(tp.PhoneList.Id, tp.PhoneList.Name)).ToArray(),
                estimatedContacts,
                estimatedTargetUsers,
                lastSync,
                t.PhotoSyncEnabled,
                t.TargetGroupId,
                t.TargetGroupName,
                t.TargetUserEmails
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
            .Include(t => t.TunnelSources)
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
            tunnel.TunnelSources.Select(s => new TunnelSourceDto(s.Id, EnumHelpers.ToPgName(s.SourceType), s.SourceIdentifier, s.SourceDisplayName, s.SourceSmtpAddress, s.SourceFilterPlain)).ToArray(),
            EnumHelpers.ToPgName(tunnel.Status),
            EnumHelpers.ToPgName(tunnel.StalePolicy),
            tunnel.StaleHoldDays,
            tunnel.FieldProfileId,
            tunnel.FieldProfile?.Name,
            tunnel.TunnelPhoneLists.Select(tp => new TunnelTargetListDto(tp.PhoneList.Id, tp.PhoneList.Name)).ToArray(),
            tunnel.CreatedAt,
            tunnel.UpdatedAt,
            tunnel.PhotoSyncEnabled,
            tunnel.TargetGroupId,
            tunnel.TargetGroupName,
            tunnel.TargetUserEmails
        );

        return Ok(dto);
    }

    /// <summary>
    /// POST /api/tunnels — Create a new tunnel with one or more DDG sources.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTunnelRequest request)
    {
        if (request.Sources is null || request.Sources.Length == 0)
            return BadRequest(new { message = "At least one source is required." });

        if (!EnumHelpers.TryFromPgName<StalePolicy>(request.StalePolicy, out var stalePolicy))
            return BadRequest(new { message = $"Invalid StalePolicy: {request.StalePolicy}" });

        var tunnel = new Tunnel
        {
            Name = request.Name,
            FieldProfileId = request.FieldProfileId,
            StalePolicy = stalePolicy,
            StaleHoldDays = request.StaleDays,
            PhotoSyncEnabled = request.PhotoSyncEnabled,
            TargetGroupId = request.TargetGroupId,
            TargetGroupName = request.TargetGroupName,
            TargetUserEmails = request.TargetUserEmails,
            Status = TunnelStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Tunnels.Add(tunnel);
        await _db.SaveChangesAsync();

        // Create TunnelSource records
        foreach (var src in request.Sources)
        {
            if (!EnumHelpers.TryFromPgName<SourceType>(src.SourceType, out var sourceType))
                return BadRequest(new { message = $"Invalid SourceType: {src.SourceType}" });

            _db.TunnelSources.Add(new TunnelSource
            {
                TunnelId = tunnel.Id,
                SourceType = sourceType,
                SourceIdentifier = src.SourceIdentifier,
                SourceDisplayName = src.SourceDisplayName,
                SourceSmtpAddress = src.SourceSmtpAddress,
                SourceFilterPlain = src.SourceFilterPlain,
                CreatedAt = DateTime.UtcNow
            });
        }

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
    /// PUT /api/tunnels/{id} — Update tunnel properties including name, sources, targets, field profile, and stale policy.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateTunnelRequest request)
    {
        var tunnel = await _db.Tunnels.FindAsync(id);
        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {id} not found." });

        if (request.Sources is null || request.Sources.Length == 0)
            return BadRequest(new { message = "At least one source is required." });

        if (!EnumHelpers.TryFromPgName<StalePolicy>(request.StalePolicy, out var stalePolicy))
            return BadRequest(new { message = $"Invalid StalePolicy: {request.StalePolicy}" });

        tunnel.Name = request.Name;
        tunnel.FieldProfileId = request.FieldProfileId;
        tunnel.StalePolicy = stalePolicy;
        tunnel.StaleHoldDays = request.StaleDays;
        tunnel.PhotoSyncEnabled = request.PhotoSyncEnabled;
        tunnel.TargetGroupId = request.TargetGroupId;
        tunnel.TargetGroupName = request.TargetGroupName;
        tunnel.TargetUserEmails = request.TargetUserEmails;
        tunnel.UpdatedAt = DateTime.UtcNow;

        // Replace TunnelSource records
        var existingSources = await _db.TunnelSources
            .Where(ts => ts.TunnelId == id)
            .ToListAsync();
        _db.TunnelSources.RemoveRange(existingSources);

        foreach (var src in request.Sources)
        {
            if (!EnumHelpers.TryFromPgName<SourceType>(src.SourceType, out var sourceType))
                return BadRequest(new { message = $"Invalid SourceType: {src.SourceType}" });

            _db.TunnelSources.Add(new TunnelSource
            {
                TunnelId = tunnel.Id,
                SourceType = sourceType,
                SourceIdentifier = src.SourceIdentifier,
                SourceDisplayName = src.SourceDisplayName,
                SourceSmtpAddress = src.SourceSmtpAddress,
                SourceFilterPlain = src.SourceFilterPlain,
                CreatedAt = DateTime.UtcNow
            });
        }

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

        if (!EnumHelpers.TryFromPgName<TunnelStatus>(request.Status, out var status))
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

    /// <summary>
    /// POST /api/tunnels/{id}/preview — Estimate creates/updates/removals before saving changes (TUNL-07).
    /// </summary>
    [HttpPost("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, [FromBody] UpdateTunnelRequest request)
    {
        var tunnel = await _db.Tunnels
            .Include(t => t.TunnelSources)
            .Include(t => t.TunnelPhoneLists)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {id} not found." });

        var currentCount = await _db.ContactSyncStates
            .Where(c => c.TunnelId == id)
            .Select(c => c.SourceUserId)
            .Distinct()
            .CountAsync();

        var estimatedCreates = 0;
        var estimatedUpdates = 0;
        var estimatedRemovals = 0;

        // Compare source identifiers to detect source changes
        var currentSourceIds = tunnel.TunnelSources.Select(s => s.SourceIdentifier).OrderBy(s => s).ToList();
        var newSourceIds = request.Sources.Select(s => s.SourceIdentifier).OrderBy(s => s).ToList();
        var sourcesChanged = !currentSourceIds.SequenceEqual(newSourceIds);

        if (sourcesChanged)
        {
            try
            {
                var totalNewCount = 0;
                foreach (var src in request.Sources)
                {
                    if (src.SourceType == "mailbox_contacts")
                    {
                        // Count contacts in the shared mailbox
                        var contactsPage = await _graphClient.Users[src.SourceIdentifier].Contacts.GetAsync(cfg =>
                        {
                            cfg.QueryParameters.Select = ["id"];
                            cfg.QueryParameters.Top = 999;
                        });
                        totalNewCount += contactsPage?.Value?.Count ?? 0;
                    }
                    else if (src.SourceType == "org_contacts")
                    {
                        // Count tenant org contacts
                        var orgContactsPage = await _graphClient.Contacts.GetAsync(cfg =>
                        {
                            cfg.QueryParameters.Select = ["id"];
                            cfg.QueryParameters.Top = 999;
                            cfg.QueryParameters.Count = true;
                            cfg.Headers.Add("ConsistencyLevel", "eventual");
                        });
                        var orgCount = (int?)orgContactsPage?.OdataCount ?? orgContactsPage?.Value?.Count ?? 0;
                        // Subtract excluded contacts
                        var excludedCount = await _db.OrgContactFilters
                            .CountAsync(f => f.TunnelId == id && f.IsExcluded);
                        totalNewCount += Math.Max(0, orgCount - excludedCount);
                    }
                    else
                    {
                        var usersPage = await _graphClient.Users.GetAsync(cfg =>
                        {
                            cfg.QueryParameters.Filter = src.SourceIdentifier;
                            cfg.QueryParameters.Select = ["id"];
                            cfg.QueryParameters.Top = 999;
                            cfg.QueryParameters.Count = true;
                            cfg.Headers.Add("ConsistencyLevel", "eventual");
                        });
                        totalNewCount += usersPage?.Value?.Count ?? 0;
                    }
                }

                estimatedCreates = Math.Max(0, totalNewCount - currentCount);
                estimatedRemovals = Math.Max(0, currentCount - totalNewCount);
                estimatedUpdates = Math.Min(currentCount, totalNewCount);
            }
            catch
            {
                return StatusCode(503, new { message = "Unable to estimate impact." });
            }
        }
        else
        {
            estimatedCreates = 0;
            estimatedUpdates = currentCount;
            estimatedRemovals = 0;
        }

        // Check for removed target lists
        var removedListIds = tunnel.TunnelPhoneLists
            .Select(tp => tp.PhoneListId)
            .Except(request.TargetListIds)
            .ToList();

        if (removedListIds.Count > 0)
        {
            var removedContacts = await _db.ContactSyncStates
                .Where(c => c.TunnelId == id && removedListIds.Contains(c.PhoneListId))
                .Select(c => c.SourceUserId)
                .Distinct()
                .CountAsync();

            estimatedRemovals += removedContacts;
        }

        return Ok(new ImpactPreviewResponse(estimatedCreates, estimatedUpdates, estimatedRemovals));
    }

    /// <summary>
    /// POST /api/tunnels/{id}/reset-hashes — Reset data and photo hashes for a single tunnel.
    /// </summary>
    [HttpPost("{id:int}/reset-hashes")]
    public async Task<IActionResult> ResetHashes(int id)
    {
        var exists = await _db.Tunnels.AnyAsync(t => t.Id == id);
        if (!exists)
            return NotFound(new { message = $"Tunnel {id} not found." });

        var count = await _db.Database.ExecuteSqlRawAsync(
            "UPDATE contact_sync_state SET data_hash = NULL, previous_data_hash = NULL, photo_hash = NULL WHERE tunnel_id = {0} AND (data_hash IS NOT NULL OR photo_hash IS NOT NULL)", id);

        return Ok(new { count, message = $"Reset {count} contact states for tunnel {id}." });
    }

    /// <summary>
    /// POST /api/tunnels/{id}/sources/{sourceId}/refresh-ddg — Re-read DDG filter from Exchange and update source (DDG-07).
    /// </summary>
    [HttpPost("{id:int}/sources/{sourceId:int}/refresh-ddg")]
    public async Task<IActionResult> RefreshDdg(int id, int sourceId, CancellationToken ct)
    {
        var source = await _db.TunnelSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.TunnelId == id, ct);
        if (source is null)
            return NotFound(new { message = $"Source {sourceId} not found on tunnel {id}." });

        if (string.IsNullOrEmpty(source.SourceSmtpAddress))
            return BadRequest(new { message = "Source has no SMTP address." });

        var ddgInfo = await _ddgResolver.GetDdgAsync(source.SourceSmtpAddress, ct);
        if (ddgInfo is null)
            return NotFound(new { message = "DDG not found in Exchange." });

        var conversionResult = _filterConverter.Convert(ddgInfo.RecipientFilter);

        source.SourceIdentifier = conversionResult.Filter ?? source.SourceIdentifier;
        source.SourceFilterPlain = _filterConverter.ToPlainLanguage(ddgInfo.RecipientFilter);
        source.SourceDisplayName = ddgInfo.DisplayName;

        var tunnel = await _db.Tunnels.FindAsync(id);
        if (tunnel is not null) tunnel.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new RefreshDdgResponse(
            "Filter refreshed.",
            conversionResult.Filter,
            _filterConverter.ToPlainLanguage(ddgInfo.RecipientFilter)));
    }
}

public record StatusUpdateRequest(string Status);
