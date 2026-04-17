using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/cleanup")]
public class CleanupController : ControllerBase
{
    private readonly AFHSyncDbContext _db;
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<CleanupController> _logger;

    // Process multiple mailboxes concurrently for tenant-wide cleanup
    private const int MailboxParallelism = 8;

    public CleanupController(AFHSyncDbContext db, GraphServiceClient graphClient, ILogger<CleanupController> logger)
    {
        _db = db;
        _graphClient = graphClient;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/cleanup/scan — Scan contact folders for selected users.
    /// Returns all contact subfolders per user so the admin can pick which to delete.
    /// Processes mailboxes in parallel for speed on tenant-wide scans.
    /// </summary>
    [HttpPost("scan")]
    public async Task<IActionResult> Scan([FromBody] CleanupScanRequest request, CancellationToken ct)
    {
        var dbMailboxes = await _db.TargetMailboxes
            .Where(m => m.IsActive)
            .ToListAsync(ct);

        List<TargetMailbox> mailboxes;
        if (request.Emails is { Length: > 0 })
        {
            // === Specific Users path — UNCHANGED behavior ===
            var emailSet = new HashSet<string>(request.Emails, StringComparer.OrdinalIgnoreCase);
            mailboxes = dbMailboxes.Where(m => emailSet.Contains(m.Email)).ToList();

            // If any requested emails aren't in target_mailboxes yet, look them up
            // directly in Graph so cleanup works for users not yet synced.
            var foundEmails = new HashSet<string>(mailboxes.Select(m => m.Email), StringComparer.OrdinalIgnoreCase);
            var missingEmails = emailSet.Where(e => !foundEmails.Contains(e)).ToList();
            foreach (var email in missingEmails)
            {
                try
                {
                    var graphUser = await _graphClient.Users[email]
                        .GetAsync(config => config.QueryParameters.Select = ["id", "displayName", "mail"], ct);
                    if (graphUser?.Id != null)
                    {
                        mailboxes.Add(new TargetMailbox
                        {
                            EntraId = graphUser.Id,
                            Email = graphUser.Mail ?? email,
                            DisplayName = graphUser.DisplayName,
                            IsActive = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to look up {Email} in Graph for cleanup scan", email);
                }
            }
        }
        else
        {
            // === All Users path — enumerate full tenant via Graph ===
            _logger.LogInformation("Cleanup scan: enumerating full tenant via Graph...");
            mailboxes = await EnumerateTenantMailboxesAsync(dbMailboxes, ct);
        }

        // Optional allow-list filter applied to BOTH paths so admin can never accidentally
        // surface or delete personal contact folders. Null/empty list = no filter (back-compat).
        HashSet<string>? allowedFolderNames = null;
        if (request.AllowedFolderNames is { Length: > 0 })
        {
            allowedFolderNames = new HashSet<string>(request.AllowedFolderNames, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Cleanup scan: filtering by {Count} allowed folder name(s)", request.AllowedFolderNames.Length);
        }

        var results = new List<UserFoldersDto>();
        var resultsLock = new object();
        using var semaphore = new SemaphoreSlim(MailboxParallelism);

        var tasks = mailboxes.Select(async mailbox =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var response = await _graphClient.Users[mailbox.EntraId].ContactFolders
                    .GetAsync(config =>
                    {
                        config.QueryParameters.Select = ["id", "displayName"];
                        config.QueryParameters.Top = 100;
                    }, ct);

                var folders = response?.Value?
                    .Where(f => f.Id != null && f.DisplayName != null)
                    .Where(f => allowedFolderNames == null || allowedFolderNames.Contains(f.DisplayName!))
                    .Select(f => new FolderDto(f.Id!, f.DisplayName!))
                    .OrderBy(f => f.Name)
                    .ToArray() ?? [];

                if (folders.Length > 0)
                {
                    lock (resultsLock)
                    {
                        results.Add(new UserFoldersDto(
                            mailbox.Email,
                            mailbox.DisplayName,
                            mailbox.EntraId,
                            folders
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan folders for {Email}", mailbox.Email);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        return Ok(results.OrderBy(r => r.DisplayName ?? r.Email));
    }

    /// <summary>
    /// Enumerates every enabled tenant user that has a mailbox-shaped <c>mail</c> value
    /// via Graph /users (paged with PageIterator). Returns a <see cref="TargetMailbox"/> projection
    /// suitable for the existing folder-scan loop. Display name is taken from the cached
    /// <paramref name="dbMailboxes"/> when available, falling back to Graph's displayName.
    /// </summary>
    private async Task<List<TargetMailbox>> EnumerateTenantMailboxesAsync(
        List<TargetMailbox> dbMailboxes,
        CancellationToken ct)
    {
        var dbDisplayByEntraId = dbMailboxes
            .Where(m => !string.IsNullOrEmpty(m.EntraId))
            .GroupBy(m => m.EntraId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        var users = new List<User>();

        var response = await _graphClient.Users.GetAsync(config =>
        {
            // Disabled accounts excluded server-side. The mail != null clause skips
            // most non-mailbox directory objects (groups/contacts/unlicensed).
            config.QueryParameters.Filter = "accountEnabled eq true and mail ne null";
            config.QueryParameters.Top = 999;
            config.QueryParameters.Count = true;
            config.QueryParameters.Select = ["id", "displayName", "mail"];
            // Required for `ne null` advanced filter + $count.
            config.Headers.Add("ConsistencyLevel", "eventual");
        }, ct);

        if (response == null)
            return new List<TargetMailbox>();

        var pageIterator = PageIterator<User, UserCollectionResponse>
            .CreatePageIterator(
                _graphClient,
                response,
                user =>
                {
                    if (!string.IsNullOrEmpty(user.Id) && !string.IsNullOrEmpty(user.Mail))
                        users.Add(user);
                    return true;
                },
                req =>
                {
                    req.Headers.Add("ConsistencyLevel", "eventual");
                    return req;
                });

        await pageIterator.IterateAsync(ct);

        var projected = users.Select(u => new TargetMailbox
        {
            EntraId = u.Id!,
            Email = u.Mail!,
            DisplayName = (dbDisplayByEntraId.TryGetValue(u.Id!, out var cached) && !string.IsNullOrWhiteSpace(cached))
                ? cached
                : u.DisplayName,
            IsActive = true
        }).ToList();

        _logger.LogInformation("Cleanup scan: tenant enumeration returned {Count} mailbox candidates", projected.Count);
        return projected;
    }

    /// <summary>
    /// POST /api/cleanup/delete — Delete specified contact folders from specified users.
    /// Empties each folder via batch API (20 deletes/request), then deletes the folder.
    /// Processes multiple mailboxes in parallel for tenant-wide cleanup.
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> DeleteFolders([FromBody] CleanupDeleteRequest request, CancellationToken ct)
    {
        int deleted = 0;
        int failed = 0;
        var counterLock = new object();
        using var semaphore = new SemaphoreSlim(MailboxParallelism);

        var tasks = request.Items.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                // Safety: verify this is a subfolder, not the user's root Contacts folder.
                // Root folder has ParentFolderId == null. We must never delete it.
                var folder = await _graphClient.Users[item.EntraId].ContactFolders[item.FolderId]
                    .GetAsync(config => config.QueryParameters.Select = ["id", "parentFolderId", "displayName"], ct);

                if (folder?.ParentFolderId is null)
                {
                    lock (counterLock) { failed++; }
                    _logger.LogWarning("BLOCKED: refusing to delete root Contacts folder for {Email}", item.Email);
                    return;
                }

                // Use permanentDelete — the regular DELETE fails when Exchange has
                // retention policies because it tries to soft-delete to recoverable items.
                // permanentDelete bypasses retention and removes the folder + contents immediately.
                // See: https://learn.microsoft.com/en-us/graph/api/contactfolder-permanentdelete
                await _graphClient.Users[item.EntraId].ContactFolders[item.FolderId]
                    .PermanentDelete
                    .PostAsync(cancellationToken: ct);

                lock (counterLock) { deleted++; }
                _logger.LogInformation("Permanently deleted contact folder {FolderName} from {Email}",
                    item.FolderName, item.Email);
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
            {
                lock (counterLock) { failed++; }
                var code = odataEx.Error?.Code ?? "unknown";
                var msg = odataEx.Error?.Message ?? odataEx.Message;
                _logger.LogWarning("Failed to delete folder {FolderName} from {Email}: [{Code}] {Message}",
                    item.FolderName, item.Email, code, msg);
            }
            catch (Exception ex)
            {
                lock (counterLock) { failed++; }
                _logger.LogWarning(ex, "Failed to delete folder {FolderName} from {Email}",
                    item.FolderName, item.Email);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        return Ok(new { deleted, failed, message = $"Deleted {deleted} folder(s), {failed} failed." });
    }

}

public record CleanupScanRequest(string[]? Emails, string[]? AllowedFolderNames);
public record CleanupDeleteRequest(CleanupDeleteItem[] Items);
public record CleanupDeleteItem(string EntraId, string Email, string FolderId, string FolderName);
public record FolderDto(string Id, string Name);
public record UserFoldersDto(string Email, string? DisplayName, string EntraId, FolderDto[] Folders);
