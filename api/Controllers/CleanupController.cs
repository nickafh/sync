using AFHSync.Shared.Data;
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
        var mailboxes = await _db.TargetMailboxes
            .Where(m => m.IsActive)
            .ToListAsync(ct);

        if (request.Emails is { Length: > 0 })
        {
            var emailSet = new HashSet<string>(request.Emails, StringComparer.OrdinalIgnoreCase);
            mailboxes = mailboxes.Where(m => emailSet.Contains(m.Email)).ToList();
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
                // Graph won't delete a non-empty folder. Batch-delete all contacts first.
                await BatchDeleteAllContactsInFolderAsync(item.EntraId, item.FolderId, ct);

                await _graphClient.Users[item.EntraId].ContactFolders[item.FolderId]
                    .DeleteAsync(cancellationToken: ct);

                lock (counterLock) { deleted++; }
                _logger.LogInformation("Deleted contact folder {FolderName} from {Email}",
                    item.FolderName, item.Email);
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

    /// <summary>
    /// Batch-deletes all contacts inside a contact folder using Graph batch API (20 per request).
    /// Handles pagination for folders with more than 999 contacts.
    /// </summary>
    private async Task BatchDeleteAllContactsInFolderAsync(string entraId, string folderId, CancellationToken ct)
    {
        // Paginate: keep fetching and deleting until the folder is empty
        while (true)
        {
            var contacts = await _graphClient.Users[entraId].ContactFolders[folderId].Contacts
                .GetAsync(config =>
                {
                    config.QueryParameters.Select = ["id"];
                    config.QueryParameters.Top = 999;
                }, ct);

            var contactIds = contacts?.Value?
                .Where(c => c.Id != null)
                .Select(c => c.Id!)
                .ToList();

            if (contactIds is null || contactIds.Count == 0)
                break;

            _logger.LogInformation("Batch-deleting {Count} contacts from folder {FolderId} in {EntraId}",
                contactIds.Count, folderId, entraId);

            // Process in chunks of 20 (Graph batch limit)
            foreach (var chunk in contactIds.Chunk(20))
            {
                var batchContent = new BatchRequestContentCollection(_graphClient);

                foreach (var contactId in chunk)
                {
                    var requestInfo = _graphClient.Users[entraId]
                        .ContactFolders[folderId]
                        .Contacts[contactId]
                        .ToDeleteRequestInformation();
                    await batchContent.AddBatchRequestStepAsync(requestInfo);
                }

                var response = await _graphClient.Batch.PostAsync(batchContent, ct);
                if (response != null)
                {
                    var statuses = await response.GetResponsesStatusCodesAsync();
                    var failCount = statuses.Count(s => !BatchResponseContent.IsSuccessStatusCode(s.Value));
                    if (failCount > 0)
                        _logger.LogWarning("{FailCount}/{Total} batch deletes failed in folder {FolderId}",
                            failCount, chunk.Length, folderId);
                }
            }
        }
    }
}

public record CleanupScanRequest(string[]? Emails);
public record CleanupDeleteRequest(CleanupDeleteItem[] Items);
public record CleanupDeleteItem(string EntraId, string Email, string FolderId, string FolderName);
public record FolderDto(string Id, string Name);
public record UserFoldersDto(string Email, string? DisplayName, string EntraId, FolderDto[] Folders);
