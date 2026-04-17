using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using Hangfire;
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
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<CleanupController> _logger;

    // Process multiple mailboxes concurrently for tenant-wide scan.
    // The DELETE path used to share this constant — that loop now lives in
    // worker/Services/CleanupJobRunner.cs (quick-260417-48z) and has its own copy.
    private const int MailboxParallelism = 8;

    public CleanupController(
        AFHSyncDbContext db,
        GraphServiceClient graphClient,
        IBackgroundJobClient jobs,
        ILogger<CleanupController> logger)
    {
        _db = db;
        _graphClient = graphClient;
        _jobs = jobs;
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
    /// POST /api/cleanup/delete — Enqueue an asynchronous tenant-wide cleanup job.
    ///
    /// Inserts a CleanupJob row in Status=Queued, hands the work to a Hangfire job
    /// (executed by the worker process — see worker/Services/CleanupJobRunner.cs),
    /// and returns 202 Accepted with { jobId, total } in milliseconds. The HTTP
    /// request is no longer bound to the Graph delete loop, which previously died
    /// at the nginx/HTTP timeout boundary on tenant-scale wipes (4900+ folders,
    /// 25-45 min). Frontend polls GET /api/cleanup/jobs/{jobId} for progress.
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> DeleteFolders([FromBody] CleanupDeleteRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Length == 0)
            return BadRequest(new { message = "items must be a non-empty array" });

        var job = new CleanupJob
        {
            Id = Guid.NewGuid(),
            Status = CleanupJobStatus.Queued,
            Total = request.Items.Length,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.CleanupJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        // Map controller DTOs to the shared CleanupJobItem record so Hangfire
        // serializes a payload that the worker can deserialize without a
        // project reference back to api/.
        var items = request.Items
            .Select(i => new CleanupJobItem(i.EntraId, i.Email, i.FolderId, i.FolderName))
            .ToArray();

        // Hangfire serializes (jobId, items) into the job payload; the worker
        // resolves ICleanupJobRunner from its own DI container and invokes RunAsync.
        // CancellationToken.None is intentional — Hangfire does not propagate the
        // request token to the worker, and v1 does not surface mid-run cancellation.
        _jobs.Enqueue<ICleanupJobRunner>(runner => runner.RunAsync(job.Id, items, CancellationToken.None));

        _logger.LogInformation("Enqueued cleanup job {JobId} with {Count} item(s)", job.Id, items.Length);
        return Accepted(new { jobId = job.Id, total = job.Total });
    }

    /// <summary>
    /// GET /api/cleanup/jobs/{jobId} — Poll endpoint for the frontend progress modal.
    /// Returns the CleanupJob row's current status + counters; 404 if the id is
    /// unknown (job never existed or has been purged).
    /// </summary>
    [HttpGet("jobs/{jobId:guid}")]
    public async Task<IActionResult> GetJob(Guid jobId, CancellationToken ct)
    {
        var job = await _db.CleanupJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();
        return Ok(new CleanupJobDto(
            job.Id,
            job.Status.ToString(),
            job.Total,
            job.Deleted,
            job.Failed,
            job.LastError,
            job.StartedAt,
            job.CompletedAt));
    }

}

public record CleanupScanRequest(string[]? Emails, string[]? AllowedFolderNames);
public record CleanupDeleteRequest(CleanupDeleteItem[] Items);
public record CleanupDeleteItem(string EntraId, string Email, string FolderId, string FolderName);
public record FolderDto(string Id, string Name);
public record UserFoldersDto(string Email, string? DisplayName, string EntraId, FolderDto[] Folders);
public record CleanupJobDto(
    Guid Id,
    string Status,
    int Total,
    int Deleted,
    int Failed,
    string? LastError,
    DateTime? StartedAt,
    DateTime? CompletedAt);
