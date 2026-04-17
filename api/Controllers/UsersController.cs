using AFHSync.Api.DTOs;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Api.Controllers;

/// <summary>
/// Operator-facing diagnostic endpoints for inspecting a specific user's
/// Exchange mailbox state. Used by the admin UI's User Lookup page to
/// triage "I'm not getting contacts" reports without ad-hoc Graph queries.
/// </summary>
[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly AFHSyncDbContext _db;
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        AFHSyncDbContext db,
        GraphServiceClient graphClient,
        ILogger<UsersController> logger)
    {
        _db = db;
        _graphClient = graphClient;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/users/{email}/folder-state
    /// For the given user, returns their Exchange contact folders (name + Graph count),
    /// matched to AFH Sync tunnels where applicable, plus last-sync timestamps from
    /// our ContactSyncStates table. Surfaces "orphan tunnels" where we have sync state
    /// for a tunnel but no matching Graph folder (the diagnostic gold nugget — suggests
    /// the folder was deleted client-side or never provisioned).
    /// </summary>
    [HttpGet("{email}/folder-state")]
    public async Task<ActionResult<UserFolderStateDto>> GetFolderState(
        string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { message = "Email is required." });

        // Step 1: Resolve user from Graph. Also gives us displayName + entraId.
        Microsoft.Graph.Models.User? graphUser;
        try
        {
            graphUser = await _graphClient.Users[email].GetAsync(config =>
            {
                config.QueryParameters.Select = ["id", "displayName", "mail", "userPrincipalName"];
            }, ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return NotFound(new { message = $"No Entra user found for {email}." });
        }
        catch (ODataError ex)
        {
            _logger.LogWarning(ex, "Graph user lookup failed for {Email}: {Status}", email, ex.ResponseStatusCode);
            return StatusCode(503, new { message = "Graph is unavailable. Try again in a moment." });
        }

        if (graphUser == null)
            return NotFound(new { message = $"No Entra user found for {email}." });

        var resolvedEntraId = graphUser.Id;
        var resolvedDisplayName = graphUser.DisplayName;
        var resolvedEmail = graphUser.Mail ?? graphUser.UserPrincipalName ?? email;

        // Step 2: Lookup our TargetMailbox row (case-insensitive on email).
        // Not found just means we haven't targeted this mailbox yet — still return folder info from Graph.
        var normalizedEmail = resolvedEmail.ToLowerInvariant();
        var targetMailbox = await _db.TargetMailboxes
            .FirstOrDefaultAsync(m => m.Email.ToLower() == normalizedEmail, ct);

        // Step 3: Fetch Graph contact folders for this mailbox.
        List<(string Id, string Name)> graphFolders = new();
        try
        {
            var response = await _graphClient.Users[email].ContactFolders.GetAsync(config =>
            {
                config.QueryParameters.Select = ["id", "displayName"];
                config.QueryParameters.Top = 200;
            }, ct);

            if (response?.Value != null)
            {
                foreach (var folder in response.Value)
                {
                    if (!string.IsNullOrEmpty(folder.Id))
                    {
                        graphFolders.Add((folder.Id, folder.DisplayName ?? "(unnamed)"));
                    }
                }
            }
        }
        catch (ODataError ex)
        {
            _logger.LogWarning(ex, "Contact folders fetch failed for {Email}: {Status}", email, ex.ResponseStatusCode);
            return StatusCode(503, new { message = "Unable to list contact folders from Graph." });
        }

        // Step 4: For each folder, fetch contact count. Using $count=true and $top=1 keeps
        // the payload tiny — we only care about OdataCount. Errors fall back to 0 so a single
        // bad folder doesn't nuke the whole response.
        var folderCounts = new Dictionary<string, int>();
        foreach (var (folderId, _) in graphFolders)
        {
            folderCounts[folderId] = await GetFolderContactCountAsync(email, folderId, ct);
        }

        // Step 5: If we track this mailbox, query our DB for per-tunnel sync state.
        // One shot: group ContactSyncStates by TunnelId, aggregate count + max(UpdatedAt).
        Dictionary<int, (int Count, DateTime LastSyncedAt, string TunnelName)> tunnelState = new();
        if (targetMailbox != null)
        {
            var states = await _db.ContactSyncStates
                .Where(s => s.TargetMailboxId == targetMailbox.Id
                         && s.GraphContactId != null
                         && s.TunnelId != null)
                .GroupBy(s => s.TunnelId!.Value)
                .Select(g => new
                {
                    TunnelId = g.Key,
                    Count = g.Count(),
                    LastSyncedAt = g.Max(s => s.UpdatedAt)
                })
                .ToListAsync(ct);

            if (states.Count > 0)
            {
                var tunnelIds = states.Select(s => s.TunnelId).ToList();
                var tunnelNames = await _db.Tunnels
                    .Where(t => tunnelIds.Contains(t.Id))
                    .Select(t => new { t.Id, t.Name })
                    .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

                foreach (var s in states)
                {
                    if (tunnelNames.TryGetValue(s.TunnelId, out var name))
                    {
                        tunnelState[s.TunnelId] = (s.Count, s.LastSyncedAt, name);
                    }
                }
            }
        }

        // Step 6: Build folder DTOs, matching folder.displayName ↔ tunnel.Name (case-insensitive).
        var matchedTunnelIds = new HashSet<int>();
        var folderDtos = new List<UserFolderDto>();
        foreach (var (folderId, folderName) in graphFolders)
        {
            int? matchedTunnelId = null;
            string? matchedTunnelName = null;
            int? expectedCount = null;
            DateTime? lastSyncedAt = null;

            foreach (var kvp in tunnelState)
            {
                if (string.Equals(kvp.Value.TunnelName, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedTunnelId = kvp.Key;
                    matchedTunnelName = kvp.Value.TunnelName;
                    expectedCount = kvp.Value.Count;
                    lastSyncedAt = kvp.Value.LastSyncedAt;
                    matchedTunnelIds.Add(kvp.Key);
                    break;
                }
            }

            folderDtos.Add(new UserFolderDto(
                FolderId: folderId,
                FolderName: folderName,
                GraphContactCount: folderCounts.TryGetValue(folderId, out var c) ? c : 0,
                MatchedTunnelId: matchedTunnelId,
                MatchedTunnelName: matchedTunnelName,
                ExpectedContactCount: expectedCount,
                LastSyncedAt: lastSyncedAt));
        }

        // Step 7: Orphan tunnels — sync state exists but no Graph folder matches.
        var orphans = tunnelState
            .Where(kvp => !matchedTunnelIds.Contains(kvp.Key))
            .Select(kvp => new UserOrphanTunnelDto(
                TunnelId: kvp.Key,
                TunnelName: kvp.Value.TunnelName,
                ExpectedContactCount: kvp.Value.Count,
                LastSyncedAt: kvp.Value.LastSyncedAt))
            .OrderBy(o => o.TunnelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new UserFolderStateDto(
            Email: resolvedEmail,
            EntraId: resolvedEntraId,
            DisplayName: resolvedDisplayName,
            IsTrackedTargetMailbox: targetMailbox != null,
            TargetMailboxId: targetMailbox?.Id,
            Folders: folderDtos
                .OrderBy(f => f.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            OrphanTunnels: orphans);

        return Ok(result);
    }

    /// <summary>
    /// Returns the contact count inside a folder. Uses $count with top=1 so the payload is tiny.
    /// Returns 0 on any error so one bad folder doesn't break the response.
    /// </summary>
    private async Task<int> GetFolderContactCountAsync(string email, string folderId, CancellationToken ct)
    {
        try
        {
            var response = await _graphClient.Users[email].ContactFolders[folderId].Contacts.GetAsync(config =>
            {
                config.QueryParameters.Count = true;
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = ["id"];
                config.Headers.Add("ConsistencyLevel", "eventual");
            }, ct);

            return (int?)response?.OdataCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Contact count fetch failed for {Email}/{FolderId}", email, folderId);
            return 0;
        }
    }
}
