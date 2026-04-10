using AFHSync.Shared.Data;
using AFHSync.Api.DTOs;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/tunnels/{tunnelId:int}/contact-exclusions")]
public class ContactExclusionsController : ControllerBase
{
    private readonly AFHSyncDbContext _db;
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<ContactExclusionsController> _logger;

    public ContactExclusionsController(
        AFHSyncDbContext db,
        GraphServiceClient graphClient,
        ILogger<ContactExclusionsController> logger)
    {
        _db = db;
        _graphClient = graphClient;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/tunnels/{tunnelId}/contact-exclusions/source-contacts
    /// Returns all source contacts for this tunnel with exclusion state.
    /// Pulls from sync state + exclusions table + source_users.
    /// </summary>
    [HttpGet("source-contacts")]
    public async Task<IActionResult> GetSourceContacts(int tunnelId)
    {
        var tunnel = await _db.Tunnels
            .Include(t => t.TunnelSources)
            .FirstOrDefaultAsync(t => t.Id == tunnelId);
        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {tunnelId} not found." });

        // Get source user IDs from sync state (currently synced contacts)
        var syncedUserIds = await _db.ContactSyncStates
            .Where(s => s.TunnelId == tunnelId)
            .Select(s => s.SourceUserId)
            .Distinct()
            .ToListAsync();

        // Get excluded entra IDs
        var exclusions = await _db.TunnelContactExclusions
            .Where(e => e.TunnelId == tunnelId)
            .ToListAsync();
        var excludedEntraIds = exclusions.Select(e => e.EntraId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Load synced source users
        var sourceUsers = await _db.SourceUsers
            .Where(u => syncedUserIds.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .ToListAsync();

        // Also load source users that are excluded but not in sync state anymore
        var syncedEntraIds = sourceUsers.Select(u => u.EntraId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingExcludedEntraIds = excludedEntraIds
            .Where(id => !syncedEntraIds.Contains(id))
            .ToList();

        if (missingExcludedEntraIds.Count > 0)
        {
            var excludedUsers = await _db.SourceUsers
                .Where(u => missingExcludedEntraIds.Contains(u.EntraId))
                .OrderBy(u => u.DisplayName)
                .ToListAsync();
            sourceUsers.AddRange(excludedUsers);
        }

        var contacts = sourceUsers
            .OrderBy(u => u.DisplayName)
            .Select(u => new SourceContactDto(
                u.Id,
                u.EntraId,
                u.DisplayName,
                u.Email,
                u.CompanyName,
                u.JobTitle,
                u.Department,
                excludedEntraIds.Contains(u.EntraId)
            )).ToList();

        return Ok(contacts);
    }

    /// <summary>
    /// POST /api/tunnels/{tunnelId}/contact-exclusions/resolve
    /// Resolves source contacts from Graph based on tunnel sources.
    /// Upserts to source_users so they're available for exclusion management.
    /// Use this before the first sync to populate the contact list.
    /// </summary>
    [HttpPost("resolve")]
    public async Task<IActionResult> ResolveContacts(int tunnelId, CancellationToken ct)
    {
        var tunnel = await _db.Tunnels
            .Include(t => t.TunnelSources)
            .FirstOrDefaultAsync(t => t.Id == tunnelId, ct);
        if (tunnel is null)
            return NotFound(new { message = $"Tunnel {tunnelId} not found." });

        var resolved = new List<SourceContactDto>();
        var exclusions = await _db.TunnelContactExclusions
            .Where(e => e.TunnelId == tunnelId)
            .Select(e => e.EntraId)
            .ToListAsync(ct);
        var excludedSet = exclusions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in tunnel.TunnelSources)
        {
            try
            {
                switch (source.SourceType)
                {
                    case SourceType.Ddg:
                        var users = await ResolveDdgUsersAsync(source.SourceIdentifier, ct);
                        resolved.AddRange(users.Select(u => new SourceContactDto(
                            0, u.entraId, u.displayName, u.email, u.companyName, u.jobTitle, u.department,
                            excludedSet.Contains(u.entraId))));
                        break;

                    case SourceType.MailboxContacts:
                        var contacts = await ResolveMailboxContactsAsync(source.SourceIdentifier, ct);
                        resolved.AddRange(contacts.Select(c => new SourceContactDto(
                            0, c.entraId, c.displayName, c.email, c.companyName, c.jobTitle, null,
                            excludedSet.Contains(c.entraId))));
                        break;

                    case SourceType.OrgContacts:
                        var orgContacts = await ResolveOrgContactsAsync(ct);
                        resolved.AddRange(orgContacts.Select(c => new SourceContactDto(
                            0, c.entraId, c.displayName, c.email, c.companyName, c.jobTitle, c.department,
                            excludedSet.Contains(c.entraId))));
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve source {SourceId} ({SourceType}) for tunnel {TunnelId}",
                    source.Id, source.SourceType, tunnelId);
            }
        }

        // Deduplicate by entraId
        var deduped = resolved
            .GroupBy(c => c.EntraId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.DisplayName)
            .ToList();

        return Ok(deduped);
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

    // --- Graph resolution helpers ---

    private async Task<List<(string entraId, string? displayName, string? email, string? companyName, string? jobTitle, string? department)>>
        ResolveDdgUsersAsync(string graphFilter, CancellationToken ct)
    {
        var results = new List<(string, string?, string?, string?, string?, string?)>();
        var response = await _graphClient.Users.GetAsync(config =>
        {
            config.QueryParameters.Filter = graphFilter;
            config.QueryParameters.Top = 999;
            config.QueryParameters.Select = ["id", "displayName", "mail", "companyName", "jobTitle", "department"];
            config.Headers.Add("ConsistencyLevel", "eventual");
            config.QueryParameters.Count = true;
        }, ct);

        if (response?.Value != null)
        {
            var pageIterator = PageIterator<User, UserCollectionResponse>
                .CreatePageIterator(_graphClient, response, user =>
                {
                    if (user.Id != null)
                        results.Add((user.Id, user.DisplayName, user.Mail, user.CompanyName, user.JobTitle, user.Department));
                    return true;
                }, req =>
                {
                    req.Headers.Add("ConsistencyLevel", "eventual");
                    return req;
                });
            await pageIterator.IterateAsync(ct);
        }
        return results;
    }

    private async Task<List<(string entraId, string? displayName, string? email, string? companyName, string? jobTitle)>>
        ResolveMailboxContactsAsync(string mailboxEmail, CancellationToken ct)
    {
        var results = new List<(string, string?, string?, string?, string?)>();
        var response = await _graphClient.Users[mailboxEmail].Contacts.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "displayName", "emailAddresses", "companyName", "jobTitle"];
            config.QueryParameters.Top = 999;
        }, ct);

        if (response?.Value != null)
        {
            foreach (var contact in response.Value)
            {
                var email = contact.EmailAddresses?.FirstOrDefault()?.Address;
                results.Add((contact.Id ?? "", contact.DisplayName, email, contact.CompanyName, contact.JobTitle));
            }
        }
        return results;
    }

    private async Task<List<(string entraId, string? displayName, string? email, string? companyName, string? jobTitle, string? department)>>
        ResolveOrgContactsAsync(CancellationToken ct)
    {
        var results = new List<(string, string?, string?, string?, string?, string?)>();
        var response = await _graphClient.Contacts.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "displayName", "mail", "companyName", "jobTitle", "department"];
            config.QueryParameters.Top = 999;
        }, ct);

        if (response?.Value != null)
        {
            var pageIterator = PageIterator<OrgContact, OrgContactCollectionResponse>
                .CreatePageIterator(_graphClient, response, orgContact =>
                {
                    if (orgContact.Id != null)
                        results.Add((orgContact.Id, orgContact.DisplayName, orgContact.Mail, orgContact.CompanyName, orgContact.JobTitle, orgContact.Department));
                    return true;
                });
            await pageIterator.IterateAsync(ct);
        }
        return results;
    }
}
