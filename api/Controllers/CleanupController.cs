using AFHSync.Shared.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/cleanup")]
public class CleanupController : ControllerBase
{
    private readonly AFHSyncDbContext _db;
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<CleanupController> _logger;

    public CleanupController(AFHSyncDbContext db, GraphServiceClient graphClient, ILogger<CleanupController> logger)
    {
        _db = db;
        _graphClient = graphClient;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/cleanup/scan — Scan contact folders for selected users.
    /// Returns all contact subfolders per user so the admin can pick which to delete.
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

        foreach (var mailbox in mailboxes)
        {
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
                    results.Add(new UserFoldersDto(
                        mailbox.Email,
                        mailbox.DisplayName,
                        mailbox.EntraId,
                        folders
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan folders for {Email}", mailbox.Email);
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// POST /api/cleanup/delete — Delete specified contact folders from specified users.
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> DeleteFolders([FromBody] CleanupDeleteRequest request, CancellationToken ct)
    {
        int deleted = 0;
        int failed = 0;

        foreach (var item in request.Items)
        {
            try
            {
                await _graphClient.Users[item.EntraId].ContactFolders[item.FolderId]
                    .DeleteAsync(cancellationToken: ct);
                deleted++;
                _logger.LogInformation("Deleted contact folder {FolderName} ({FolderId}) from {Email}",
                    item.FolderName, item.FolderId, item.Email);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to delete folder {FolderId} from {Email}", item.FolderId, item.Email);
            }
        }

        return Ok(new { deleted, failed, message = $"Deleted {deleted} folder(s), {failed} failed." });
    }
}

public record CleanupScanRequest(string[]? Emails);
public record CleanupDeleteRequest(CleanupDeleteItem[] Items);
public record CleanupDeleteItem(string EntraId, string Email, string FolderId, string FolderName);
public record FolderDto(string Id, string Name);
public record UserFoldersDto(string Email, string? DisplayName, string EntraId, FolderDto[] Folders);
